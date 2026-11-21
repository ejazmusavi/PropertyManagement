// ignore_for_file: prefer_initializing_formals
import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:http/http.dart' as http;
import 'package:http_parser/http_parser.dart';

import '../config/app_config.dart';
import '../storage/token_storage.dart';
import 'api_exception.dart';

/// A file received from the API (receipts, proofs).
class DownloadedFile {
  DownloadedFile(this.bytes, this.fileName, this.contentType);
  final Uint8List bytes;
  final String fileName;
  final String contentType;
}

/// A file picked by the customer for upload.
class UploadFile {
  UploadFile(this.fileName, this.bytes, this.contentType);
  final String fileName;
  final Uint8List bytes;
  final String contentType;
}

/// HTTP access to the ERP API: bearer token, transparent token refresh (single flight), Problem Details mapping.
/// The customer id is never sent: the server derives it from the signed-in account.
class ApiClient {


  ApiClient({required TokenStorage storage, http.Client? client, String? baseUrl, this.onSessionExpired})
      : _storage = storage,
        _http = client ?? http.Client(),
        _base = baseUrl ?? AppConfig.apiBaseUrl;

  final TokenStorage _storage;
  final http.Client _http;
  final String _base;

  /// Called when the refresh token is no longer accepted: the app must go back to the login screen.
  void Function()? onSessionExpired;

  StoredSession? _session;
  Future<bool>? _refreshing;

  bool get hasSession => _session != null;

  Future<void> restore() async => _session = await _storage.read();

  Future<void> setSession(StoredSession session) async {
    _session = session;
    await _storage.write(session);
  }

  Future<void> clearSession() async {
    _session = null;
    await _storage.clear();
  }

  String? get refreshToken => _session?.refreshToken;

  Uri _uri(String path, [Map<String, String?>? query]) {
    final q = <String, String>{};
    query?.forEach((k, v) {
      if (v != null && v.isNotEmpty) q[k] = v;
    });
    return Uri.parse('$_base${AppConfig.apiPrefix}$path').replace(queryParameters: q.isEmpty ? null : q);
  }

  Future<dynamic> get(String path, {Map<String, String?>? query}) => _send('GET', path, query: query);

  Future<dynamic> post(String path, {Object? body, bool auth = true}) => _send('POST', path, body: body, auth: auth);

  Future<dynamic> put(String path, {Object? body}) => _send('PUT', path, body: body);

  /// Multipart upload: text [fields] plus one [file] sent under [fileField].
  Future<dynamic> postMultipart(String path, {required Map<String, String> fields, required String fileField, UploadFile? file}) =>
      _send('POST', path, fields: fields, fileField: fileField, file: file);

  Future<DownloadedFile> download(String path) async {
    final response = await _execute('GET', path, raw: true);
    final disposition = response.headers['content-disposition'] ?? '';
    final match = RegExp('filename\\*?=(?:UTF-8\'\')?"?([^";]+)"?').firstMatch(disposition);
    final name = match == null ? 'download' : Uri.decodeComponent(match.group(1)!);
    return DownloadedFile(response.bodyBytes, name, response.headers['content-type'] ?? 'application/octet-stream');
  }

  Future<dynamic> _send(String method, String path,
      {Map<String, String?>? query, Object? body, bool auth = true, Map<String, String>? fields, String? fileField, UploadFile? file}) async {
    final response = await _execute(method, path, query: query, body: body, auth: auth, fields: fields, fileField: fileField, file: file);
    if (response.statusCode == 204 || response.bodyBytes.isEmpty) return null;
    return jsonDecode(utf8.decode(response.bodyBytes));
  }

  Future<http.Response> _execute(String method, String path,
      {Map<String, String?>? query, Object? body, bool auth = true, bool raw = false, Map<String, String>? fields, String? fileField, UploadFile? file}) async {
    var retried = false;
    while (true) {
      if (auth) await _ensureFreshToken();
      http.Response response;
      try {
        response = await _dispatch(method, path, query, body, auth, fields, fileField, file).timeout(AppConfig.requestTimeout);
      } on TimeoutException {
        throw ApiException('The server did not answer in time. Check your connection.', isNetwork: true);
      } on http.ClientException {
        throw ApiException('No connection to the server. Check your internet connection.', isNetwork: true);
      }
      if (response.statusCode == 401 && auth && !retried && _session != null) {
        retried = true;
        if (await _refresh()) continue;
        throw ApiException('Your session has expired. Please sign in again.', statusCode: 401);
      }
      if (response.statusCode >= 200 && response.statusCode < 300) return response;
      Map<String, dynamic>? problem;
      try {
        final decoded = jsonDecode(utf8.decode(response.bodyBytes));
        if (decoded is Map<String, dynamic>) problem = decoded;
      } catch (_) {}
      throw ApiException.fromProblem(response.statusCode, problem);
    }
  }

  Future<http.Response> _dispatch(String method, String path, Map<String, String?>? query, Object? body, bool auth, Map<String, String>? fields, String? fileField, UploadFile? file) async {
    final uri = _uri(path, query);
    final headers = <String, String>{'Accept': 'application/json, application/pdf, */*'};
    if (auth && _session != null) headers['Authorization'] = 'Bearer ${_session!.accessToken}';
    if (fields != null) {
      final request = http.MultipartRequest(method, uri)..headers.addAll(headers)..fields.addAll(fields);
      if (file != null && fileField != null) {
        request.files.add(http.MultipartFile.fromBytes(fileField, file.bytes, filename: file.fileName, contentType: _mediaType(file.contentType)));
      }
      return http.Response.fromStream(await _http.send(request));
    }
    if (body != null) headers['Content-Type'] = 'application/json';
    final encoded = body == null ? null : jsonEncode(body);
    return switch (method) {
      'GET' => _http.get(uri, headers: headers),
      'POST' => _http.post(uri, headers: headers, body: encoded),
      'PUT' => _http.put(uri, headers: headers, body: encoded),
      _ => throw ArgumentError('Unsupported method $method'),
    };
  }

  static MediaType _mediaType(String value) => MediaType.parse(value);

  Future<void> _ensureFreshToken() async {
    final s = _session;
    if (s != null && !s.accessValid && s.refreshValid) await _refresh();
  }

  /// One refresh at a time: concurrent requests wait for the same call (refresh tokens rotate and can be used once).
  Future<bool> _refresh() {
    return _refreshing ??= _doRefresh().whenComplete(() => _refreshing = null);
  }

  Future<bool> _doRefresh() async {
    final s = _session;
    if (s == null || !s.refreshValid) {
      await _expire();
      return false;
    }
    try {
      final response = await _http
          .post(_uri('/auth/refresh'), headers: {'Content-Type': 'application/json'}, body: jsonEncode({'refreshToken': s.refreshToken}))
          .timeout(AppConfig.requestTimeout);
      if (response.statusCode == 200) {
        final json = jsonDecode(utf8.decode(response.bodyBytes)) as Map<String, dynamic>;
        await setSession(sessionFromJson(json));
        return true;
      }
      if (response.statusCode == 401 || response.statusCode == 400) {
        await _expire();
        return false;
      }
      return false; // transient problem: keep the session and let the caller fail
    } on TimeoutException {
      return false;
    } on http.ClientException {
      return false;
    }
  }

  Future<void> _expire() async {
    await clearSession();
    onSessionExpired?.call();
  }

  static StoredSession sessionFromJson(Map<String, dynamic> json) => StoredSession(
        accessToken: json['accessToken'] as String,
        accessExpiresAt: DateTime.parse(json['accessTokenExpiresAtUtc'] as String).toUtc(),
        refreshToken: json['refreshToken'] as String,
        refreshExpiresAt: DateTime.parse(json['refreshTokenExpiresAtUtc'] as String).toUtc(),
      );
}
