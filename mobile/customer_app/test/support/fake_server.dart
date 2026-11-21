import 'dart:convert';

import 'package:customer_app/core/network/api_client.dart';
import 'package:customer_app/core/util/file_service.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

/// A request seen by the fake server.
class SeenRequest {
  SeenRequest(this.method, this.path, this.query, this.headers, this.body, this.multipartFields);
  final String method;
  final String path;
  final Map<String, String> query;
  final Map<String, String> headers;
  final String body;
  final String multipartFields;
}

/// A stand-in for the ERP API that answers with the same JSON shapes as the real portal endpoints (camelCase, numeric enums).
class FakeServer {
  FakeServer() {
    client = MockClient.streaming(_handle);
  }

  late final MockClient client;
  final List<SeenRequest> requests = [];

  /// Access tokens currently accepted by the server.
  String validAccess = 'access-1';
  String validRefresh = 'refresh-1';
  int refreshCalls = 0;
  bool refreshRejected = false;
  bool onlineEnabled = false;
  int proofPosts = 0;
  String? lastProofFields;

  static const accessExpiry = '2099-01-01T00:00:00Z';

  Map<String, dynamic> tokens() => {'accessToken': validAccess, 'accessTokenExpiresAtUtc': accessExpiry, 'refreshToken': validRefresh, 'refreshTokenExpiresAtUtc': accessExpiry};

  Future<http.StreamedResponse> _handle(http.BaseRequest request, http.ByteStream stream) async {
    final bytes = await stream.toBytes();
    final body = utf8.decode(bytes, allowMalformed: true);
    final path = request.url.path.replaceFirst('/api/v1', '');
    requests.add(SeenRequest(request.method, path, request.url.queryParameters, request.headers, body, request is http.MultipartRequest ? body : ''));

    http.StreamedResponse json(int status, Object? data) =>
        http.StreamedResponse(Stream.value(utf8.encode(data == null ? '' : jsonEncode(data))), status, headers: {'content-type': 'application/json'});

    // ---- anonymous ----
    if (path == '/auth/login') {
      final c = jsonDecode(body) as Map<String, dynamic>;
      if (c['email'] == 'customer@example.com' && c['password'] == 'Str0ng#Passw0rd!') return json(200, tokens());
      if (c['email'] == 'staff@example.com') return json(200, tokens());
      return json(401, {'title': 'Authentication failed', 'detail': 'Invalid email or password.', 'status': 401});
    }
    if (path == '/auth/refresh') {
      refreshCalls++;
      if (refreshRejected) return json(401, {'title': 'Authentication failed', 'status': 401});
      validAccess = 'access-${refreshCalls + 1}';
      validRefresh = 'refresh-${refreshCalls + 1}';
      return json(200, tokens());
    }
    if (path == '/auth/forgot-password') return json(204, null);
    if (path == '/auth/reset-password') {
      final c = jsonDecode(body) as Map<String, dynamic>;
      return c['token'] == 'GOODCODE' ? json(204, null) : json(400, {'title': 'One or more validation errors occurred.', 'status': 400, 'errors': {'NewPassword': ['The reset code is invalid or has expired.']}});
    }

    // ---- authenticated ----
    final auth = request.headers['Authorization'];
    if (auth != 'Bearer $validAccess') return json(401, {'title': 'Unauthorized', 'status': 401});
    switch ('${request.method} $path') {
      case 'GET /auth/me':
        final staff = requests.any((r) => r.path == '/auth/login' && r.body.contains('staff@example.com'));
        return json(200, {'id': 'u1', 'email': 'customer@example.com', 'fullName': 'Ali Khan', 'roles': [staff ? 'Accountant' : 'Customer'], 'permissions': [staff ? 'payments.view' : 'portal.access']});
      case 'POST /auth/logout':
        return json(204, null);
      case 'GET /portal/dashboard':
        return json(200, dashboard());
      case 'GET /portal/properties':
        return json(200, [property()]);
      case 'GET /portal/agreements/7':
        return json(200, agreement());
      case 'GET /portal/agreements/7/schedule':
        return json(200, schedule());
      case 'GET /portal/payments':
        return json(200, {'items': [payment()], 'page': 1, 'pageSize': 20, 'totalCount': 1});
      case 'GET /portal/payments/11':
        return json(200, paymentDetail());
      case 'GET /portal/payment-instructions':
        return json(200, instructions());
      case 'POST /portal/payments':
        proofPosts++;
        lastProofFields = body;
        return json(201, paymentDetail(status: 1));
      case 'GET /portal/ledger':
        return json(200, ledger());
      case 'GET /portal/agreements/7/ledger':
        return json(200, agreementLedger());
      case 'GET /portal/notifications':
        return json(200, {'notifications': {'items': [notification()], 'page': 1, 'pageSize': 30, 'totalCount': 1}, 'unread': 1});
      case 'POST /portal/notifications/5/read':
      case 'POST /portal/notifications/read-all':
        return json(204, null);
      case 'GET /portal/announcements':
        return json(200, [{'id': 1, 'title': 'Office closed Friday', 'body': 'We are closed on Friday.', 'publishedAtUtc': '2026-11-20T08:00:00Z', 'expiresAtUtc': null}]);
      case 'GET /portal/support-requests':
        return json(200, [supportRequest()]);
      case 'GET /portal/support-requests/3':
        return json(200, supportDetail());
      case 'POST /portal/support-requests':
        return json(201, supportDetail());
      case 'GET /portal/profile':
        return json(200, {'customerCode': 'CUS-000001', 'fullName': 'Ali Khan', 'identityNumberMasked': '*********4567', 'mobile': '0300-1234567', 'email': 'ali@example.com', 'address': 'Murree', 'loginEmail': 'customer@example.com'});
      case 'GET /portal/receipts/21/pdf':
        return http.StreamedResponse(Stream.value(utf8.encode('%PDF-1.4 fake')), 200, headers: {'content-type': 'application/pdf', 'content-disposition': 'attachment; filename="RCP-000021.pdf"'});
    }
    return json(404, {'title': 'Not Found', 'status': 404});
  }

  // ---------------------------------------------------------------- payloads (shapes of the C# DTOs)
  Map<String, dynamic> dashboard() => {
        'customerName': 'Ali Khan', 'customerCode': 'CUS-000001', 'currencySymbol': 'Rs', 'totalProperties': 1, 'activeAgreements': 1, 'totalAgreementValue': 5000000.0,
        'paidAmount': 600000.0, 'outstandingAmount': 162500.0, 'overdueAmount': 162500.0, 'notYetDueAmount': 4237500.0, 'advanceBalance': 0.0, 'unreadNotifications': 1,
        'onlinePaymentsEnabled': onlineEnabled,
        'upcomingDues': [{'agreementId': 7, 'agreementNumber': 'AGR-000007', 'itemId': 1, 'lineNo': 2, 'description': 'Monthly installment', 'dueDate': '2026-10-01', 'amount': 162500.0, 'outstanding': 162500.0, 'isOverdue': true}],
      };

  Map<String, dynamic> property() => {
        'propertyId': 3, 'propertyCode': 'PLT-101', 'propertyNumber': '101', 'projectCode': 'GV', 'projectName': 'Green Valley', 'category': 'Residential', 'propertyType': 'Residential Plot',
        'size': 5.0, 'unit': 2, 'sharePercent': 100.0, 'agreementId': 7, 'agreementNumber': 'AGR-000007', 'bookingNumber': 'BK-000007', 'bookingStatus': 3, 'agreementStatus': 1, 'paymentMode': 2,
        'bookingDate': '2026-09-01', 'linePrice': 5000000.0,
      };

  Map<String, dynamic> summary() => {
        'agreementValue': 5000000.0, 'discounts': 0.0, 'charges': 0.0, 'contractTotal': 5000000.0, 'scheduledTotal': 5000000.0, 'dueToDate': 762500.0, 'paymentsReceived': 600000.0,
        'paymentsReversed': 0.0, 'waivers': 0.0, 'penaltiesAccrued': 0.0, 'refunds': 0.0, 'outstanding': 162500.0, 'overdue': 162500.0, 'notYetDue': 4237500.0, 'advanceBalance': 0.0,
        'unallocatedBalance': 0.0, 'ledgerBalance': 162500.0, 'reconciled': true,
      };

  Map<String, dynamic> agreement() => {
        'agreementId': 7, 'agreementNumber': 'AGR-000007', 'status': 1, 'versionNo': 1, 'generatedAtUtc': '2026-09-01T10:00:00Z', 'bookingNumber': 'BK-000007', 'bookingStatus': 3,
        'bookingDate': '2026-09-01', 'paymentMode': 2, 'projectCode': 'GV', 'projectName': 'Green Valley', 'currencySymbol': 'Rs', 'sharePercent': 100.0, 'isHolder': true,
        'properties': [{'propertyId': 3, 'propertyCode': 'PLT-101', 'propertyNumber': '101', 'size': 5.0, 'unit': 2, 'linePrice': 5000000.0}], 'summary': summary(), 'agreementText': 'AGREEMENT TEXT',
      };

  Map<String, dynamic> schedule() => {
        'agreementId': 7, 'agreementNumber': 'AGR-000007', 'agreementStatus': 1, 'currencySymbol': 'Rs',
        'summary': {'totalAmount': 5000000.0, 'paid': 600000.0, 'waived': 0.0, 'outstanding': 4400000.0, 'outstandingDue': 162500.0, 'overdue': 162500.0, 'notYetDue': 4237500.0, 'paidCount': 1, 'outstandingCount': 2, 'overdueCount': 1},
        'items': [
          {'itemId': 1, 'lineNo': 1, 'installmentNo': 1, 'installmentCount': 1, 'description': 'Down payment', 'dueDate': '2026-09-01', 'amount': 500000.0, 'paid': 500000.0, 'waived': 0.0, 'outstanding': 0.0, 'lastPaymentDate': '2026-09-01', 'status': 'Paid', 'isOverdue': false},
          {'itemId': 2, 'lineNo': 2, 'installmentNo': 1, 'installmentCount': 24, 'description': 'Monthly installment', 'dueDate': '2026-10-01', 'amount': 162500.0, 'paid': 0.0, 'waived': 0.0, 'outstanding': 162500.0, 'lastPaymentDate': null, 'status': 'Overdue', 'isOverdue': true},
          {'itemId': 3, 'lineNo': 3, 'installmentNo': 2, 'installmentCount': 24, 'description': 'Monthly installment', 'dueDate': '2099-01-01', 'amount': 162500.0, 'paid': 0.0, 'waived': 0.0, 'outstanding': 162500.0, 'lastPaymentDate': null, 'status': 'Upcoming', 'isOverdue': false},
        ],
      };

  Map<String, dynamic> payment({int status = 3}) => {
        'id': 11, 'paymentNumber': 'PAY-000011', 'agreementId': 7, 'agreementNumber': 'AGR-000007', 'amount': 600000.0, 'currencyCode': 'PKR', 'paymentDate': '2026-09-01', 'methodName': 'Bank transfer',
        'reference': 'TRX-1001', 'status': status, 'source': 2, 'submittedAtUtc': '2026-09-01T10:00:00Z', 'receiptId': status == 3 ? 21 : null, 'receiptNumber': status == 3 ? 'RCP-000021' : null,
        'rejectReason': null, 'proofCount': 1,
      };

  Map<String, dynamic> paymentDetail({int status = 3}) => {
        'payment': payment(status: status), 'currencySymbol': 'Rs', 'allocated': 600000.0, 'advance': 0.0, 'unallocated': 0.0,
        'allocations': [{'description': 'Line 1: Down payment', 'dueDate': '2026-09-01', 'amount': 500000.0, 'status': 'Active'}, {'description': 'Line 2: Monthly installment', 'dueDate': '2026-10-01', 'amount': 100000.0, 'status': 'Active'}],
        'proofs': [{'id': 31, 'fileName': 'slip.png', 'contentType': 'image/png', 'sizeBytes': 2048, 'uploadedAtUtc': '2026-09-01T10:00:00Z'}], 'verificationNote': null,
      };

  Map<String, dynamic> instructions() => {
        'note': 'Pay to the company account and submit the slip.',
        'bankAccounts': [{'bankName': 'HBL', 'branch': 'Murree', 'accountTitle': 'Green Valley Developers', 'accountNumber': '0012-3456789', 'iban': 'PK00HABB0000001234567890', 'currencyCode': 'PKR'}],
        'methods': [{'id': 2, 'code': 'BANK_TRANSFER', 'name': 'Bank transfer', 'kind': 2, 'requiresReference': true}, {'id': 1, 'code': 'CASH', 'name': 'Cash deposit', 'kind': 1, 'requiresReference': false}],
        'onlinePaymentsEnabled': onlineEnabled,
      };

  Map<String, dynamic> _entry(int id, int type, String description, double debit, double credit, double balance) => {
        'id': id, 'entryDate': '2026-09-01', 'postedAtUtc': '2026-09-01T10:00:00Z', 'entryType': type, 'description': description, 'debit': debit, 'credit': credit, 'balance': balance,
        'agreementId': 7, 'agreementNumber': 'AGR-000007', 'paymentId': null, 'paymentNumber': null, 'refundId': null, 'refundNumber': null,
      };

  Map<String, dynamic> ledger() => {
        'customerId': 1, 'customerCode': 'CUS-000001', 'customerName': 'Ali Khan', 'currencySymbol': 'Rs', 'totalDebit': 762500.0, 'totalCredit': 600000.0, 'balance': 162500.0,
        'outstanding': 162500.0, 'overdue': 162500.0, 'advanceBalance': 0.0, 'reconciled': true,
        'agreements': [{'agreementId': 7, 'agreementNumber': 'AGR-000007', 'projectCode': 'GV', 'status': 1, 'isHolder': true, 'balance': 162500.0, 'outstanding': 162500.0, 'overdue': 162500.0, 'advanceBalance': 0.0}],
        'entries': {'items': [_entry(2, 3, 'Payment PAY-000011 received', 0.0, 600000.0, 162500.0), _entry(1, 1, 'Installments due', 762500.0, 0.0, 762500.0)], 'page': 1, 'pageSize': 30, 'totalCount': 2},
      };

  Map<String, dynamic> agreementLedger() => {
        'agreementId': 7, 'agreementNumber': 'AGR-000007', 'currencySymbol': 'Rs', 'properties': ['PLT-101'], 'summary': summary(),
        'entries': {'items': [_entry(1, 1, 'Installments due', 762500.0, 0.0, 762500.0)], 'page': 1, 'pageSize': 30, 'totalCount': 1},
      };

  Map<String, dynamic> notification() => {'id': 5, 'kind': 2, 'title': 'Payment received', 'message': 'Your payment PAY-000011 was approved.', 'relatedType': 'Payment', 'relatedId': 11, 'createdAtUtc': '2026-09-02T09:00:00Z', 'isRead': false};

  Map<String, dynamic> supportRequest() => {'id': 3, 'requestNumber': 'SUP-000003', 'category': 2, 'subject': 'Receipt missing', 'status': 2, 'agreementId': 7, 'agreementNumber': 'AGR-000007', 'createdAtUtc': '2026-09-02T09:00:00Z', 'lastActivityAtUtc': '2026-09-03T09:00:00Z', 'messageCount': 2};

  Map<String, dynamic> supportDetail() => {
        'request': supportRequest(),
        'messages': [
          {'id': 1, 'authorType': 1, 'authorName': 'Ali Khan', 'body': 'I did not get my receipt.', 'createdAtUtc': '2026-09-02T09:00:00Z'},
          {'id': 2, 'authorType': 2, 'authorName': 'support', 'body': 'We will e-mail it today.', 'createdAtUtc': '2026-09-03T09:00:00Z'},
        ],
        'rowVersion': 'AAAA',
      };
}

class FakeFileService implements FileService {
  final List<DownloadedFile> opened = [];

  @override
  Future<void> open(DownloadedFile file) async => opened.add(file);
}
