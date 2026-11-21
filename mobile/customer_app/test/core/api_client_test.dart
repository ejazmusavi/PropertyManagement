import 'dart:convert';

import 'package:customer_app/core/network/api_client.dart';
import 'package:customer_app/core/network/api_exception.dart';
import 'package:customer_app/core/storage/token_storage.dart';
import 'package:customer_app/core/util/format.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../support/fake_server.dart';

StoredSession _session({String access = 'access-1', String refresh = 'refresh-1', bool accessExpired = false}) => StoredSession(
      accessToken: access,
      accessExpiresAt: DateTime.now().toUtc().add(Duration(hours: accessExpired ? -1 : 1)),
      refreshToken: refresh,
      refreshExpiresAt: DateTime.now().toUtc().add(const Duration(days: 7)),
    );

void main() {
  late FakeServer server;
  late MemoryTokenStorage storage;
  late ApiClient api;
  var expired = 0;

  setUp(() {
    server = FakeServer();
    storage = MemoryTokenStorage();
    expired = 0;
    api = ApiClient(storage: storage, client: server.client, baseUrl: 'http://test', onSessionExpired: () => expired++);
  });

  test('sends the bearer token and never a customer id', () async {
    await api.setSession(_session());
    await api.get('/portal/dashboard');
    await api.get('/portal/payments', query: {'page': '1'});
    await api.post('/portal/support-requests', body: {'category': 1, 'subject': 'Hello', 'message': 'Hi there', 'agreementId': null});
    final authed = server.requests.where((r) => r.path.startsWith('/portal'));
    expect(authed, isNotEmpty);
    for (final r in authed) {
      expect(r.headers['Authorization'], 'Bearer access-1');
      expect(r.path.toLowerCase().contains('customer'), isFalse);
      expect(r.body.toLowerCase().contains('customerid'), isFalse);
      expect(r.query.keys.any((k) => k.toLowerCase().contains('customer')), isFalse);
    }
  });

  test('an expired access token is refreshed before the request', () async {
    await api.setSession(_session(access: 'stale', accessExpired: true));
    final json = await api.get('/portal/dashboard') as Map<String, dynamic>;
    expect(json['customerName'], 'Ali Khan');
    expect(server.refreshCalls, 1);
    expect((await storage.read())!.refreshToken, 'refresh-2'); // the rotated refresh token is stored
  });

  test('a 401 triggers one refresh and a retry; concurrent requests share the refresh', () async {
    await api.setSession(_session(access: 'revoked'));
    final results = await Future.wait([api.get('/portal/dashboard'), api.get('/portal/properties'), api.get('/portal/profile')]);
    expect(results, everyElement(isNotNull));
    expect(server.refreshCalls, 1);
    expect(expired, 0);
  });

  test('a rejected refresh token ends the session', () async {
    server.refreshRejected = true;
    await api.setSession(_session(access: 'revoked'));
    await expectLater(api.get('/portal/dashboard'), throwsA(isA<ApiException>().having((e) => e.isUnauthorized, 'unauthorized', true)));
    expect(expired, 1);
    expect(await storage.read(), isNull);
    expect(api.hasSession, isFalse);
  });

  test('Problem Details become readable errors', () async {
    final e = await api.post('/auth/reset-password', body: {'email': 'a@b.com', 'token': 'bad', 'newPassword': 'x'}, auth: false).then<ApiException?>((_) => null, onError: (Object e) => e as ApiException);
    expect(e, isNotNull);
    expect(e!.statusCode, 400);
    expect(e.displayMessage, 'The reset code is invalid or has expired.');
    final login = await api.post('/auth/login', body: {'email': 'x@y.com', 'password': 'nope'}, auth: false).then<ApiException?>((_) => null, onError: (Object e) => e as ApiException);
    expect(login!.displayMessage, 'Invalid email or password.');
    expect(ApiException.fromProblem(423, {'title': 'x'}).message, 'x');
    expect(ApiException.fromProblem(429, null).message, contains('Too many'));
  });

  test('network failures are reported as such', () async {
    final offline = ApiClient(storage: storage, client: MockClient((_) async => throw http.ClientException('down')), baseUrl: 'http://test');
    await expectLater(offline.post('/auth/login', body: {}, auth: false), throwsA(isA<ApiException>().having((e) => e.isNetwork, 'network', true)));
  });

  test('downloads keep the file name from the server', () async {
    await api.setSession(_session());
    final file = await api.download('/portal/receipts/21/pdf');
    expect(file.fileName, 'RCP-000021.pdf');
    expect(utf8.decode(file.bytes), startsWith('%PDF'));
    expect(file.contentType, 'application/pdf');
  });

  test('amounts typed by the customer are validated to two decimals', () {
    expect(Fmt.parseAmount('1,500.5'), 1500.5);
    expect(Fmt.parseAmount('250000'), 250000);
    expect(Fmt.parseAmount('0'), isNull);
    expect(Fmt.parseAmount('-5'), isNull);
    expect(Fmt.parseAmount('10.005'), isNull);
    expect(Fmt.parseAmount('abc'), isNull);
    expect(Fmt.money(1234567.5, 'Rs'), 'Rs 1,234,567.50');
  });
}
