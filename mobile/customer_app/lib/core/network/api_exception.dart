/// An error answered by the API (RFC 7807 Problem Details) or a network failure.
class ApiException implements Exception {
  ApiException(this.message, {this.statusCode, this.fieldErrors = const {}, this.isNetwork = false});

  final String message;
  final int? statusCode;
  final Map<String, List<String>> fieldErrors;
  final bool isNetwork;

  bool get isUnauthorized => statusCode == 401;
  bool get isForbidden => statusCode == 403;
  bool get isNotFound => statusCode == 404;

  /// Message for the user: field messages are appended so that validation problems are readable.
  String get displayMessage {
    if (fieldErrors.isEmpty) return message;
    final details = fieldErrors.values.expand((e) => e).toSet().join(' ');
    return details.isEmpty ? message : details;
  }

  factory ApiException.fromProblem(int status, Map<String, dynamic>? body) {
    final errors = <String, List<String>>{};
    final raw = body?['errors'];
    if (raw is Map) {
      raw.forEach((k, v) => errors[k.toString()] = v is List ? v.map((e) => e.toString()).toList() : [v.toString()]);
    }
    final detail = body?['detail']?.toString();
    final title = body?['title']?.toString();
    return ApiException(detail ?? title ?? _defaultMessage(status), statusCode: status, fieldErrors: errors);
  }

  static String _defaultMessage(int status) => switch (status) {
        401 => 'Please sign in again.',
        403 => 'You do not have access to this.',
        404 => 'Not found.',
        423 => 'Account is temporarily locked. Try again later.',
        429 => 'Too many attempts. Please wait a moment.',
        >= 500 => 'The server had a problem. Please try again.',
        _ => 'Something went wrong.',
      };

  @override
  String toString() => 'ApiException($statusCode): $message';
}
