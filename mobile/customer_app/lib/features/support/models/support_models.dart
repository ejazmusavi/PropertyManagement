import '../../../core/util/json.dart';

enum SupportCategory {
  general(1, 'General'),
  payment(2, 'Payment'),
  agreement(3, 'Agreement'),
  documents(4, 'Documents'),
  complaint(5, 'Complaint');

  const SupportCategory(this.code, this.label);
  final int code;
  final String label;
}

enum SupportStatus {
  open(1, 'Open'),
  inProgress(2, 'In progress'),
  resolved(3, 'Resolved'),
  closed(4, 'Closed');

  const SupportStatus(this.code, this.label);
  final int code;
  final String label;
}

class SupportMessage {
  SupportMessage({required this.fromOffice, required this.authorName, required this.body, required this.createdAt});

  factory SupportMessage.fromJson(Map<String, dynamic> j) =>
      SupportMessage(fromOffice: J.integer(j['authorType']) == 2, authorName: J.str(j['authorName']), body: J.str(j['body']), createdAt: J.date(j['createdAtUtc']));

  final bool fromOffice;
  final String authorName;
  final String body;
  final DateTime createdAt;
}

class SupportRequest {
  SupportRequest({required this.id, required this.requestNumber, required this.category, required this.subject, required this.status, required this.agreementNumber, required this.lastActivityAt, required this.messageCount});

  factory SupportRequest.fromJson(Map<String, dynamic> j) => SupportRequest(
        id: J.integer(j['id']),
        requestNumber: J.str(j['requestNumber']),
        category: enumFrom(SupportCategory.values, (e) => e.code, j['category'], SupportCategory.general),
        subject: J.str(j['subject']),
        status: enumFrom(SupportStatus.values, (e) => e.code, j['status'], SupportStatus.open),
        agreementNumber: J.strOrNull(j['agreementNumber']),
        lastActivityAt: J.date(j['lastActivityAtUtc']),
        messageCount: J.integer(j['messageCount']),
      );

  final int id;
  final String requestNumber;
  final SupportCategory category;
  final String subject;
  final SupportStatus status;
  final String? agreementNumber;
  final DateTime lastActivityAt;
  final int messageCount;
}

class SupportDetail {
  SupportDetail({required this.request, required this.messages});

  factory SupportDetail.fromJson(Map<String, dynamic> j) =>
      SupportDetail(request: SupportRequest.fromJson(J.map(j['request'])), messages: J.list(j['messages']).map(SupportMessage.fromJson).toList());

  final SupportRequest request;
  final List<SupportMessage> messages;
}
