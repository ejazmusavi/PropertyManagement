import '../../../core/util/json.dart';
import '../../ledger/models/ledger_models.dart';

enum BookingStatus {
  draft(1, 'Draft'),
  pendingApproval(2, 'Pending approval'),
  confirmed(3, 'Confirmed'),
  rejected(4, 'Rejected'),
  cancelled(5, 'Cancelled');

  const BookingStatus(this.code, this.label);
  final int code;
  final String label;
}

enum AgreementStatus {
  active(1, 'Active'),
  cancelled(2, 'Cancelled');

  const AgreementStatus(this.code, this.label);
  final int code;
  final String label;
}

enum PaymentMode {
  cash(1, 'Cash'),
  installment(2, 'Installments');

  const PaymentMode(this.code, this.label);
  final int code;
  final String label;
}

const measurementUnits = {1: 'Kanal', 2: 'Marla', 3: 'sq ft', 4: 'sq yd', 5: 'sq m', 6: 'Acre'};

class PortalProperty {
  PortalProperty({
    required this.propertyId,
    required this.propertyCode,
    required this.propertyNumber,
    required this.projectCode,
    required this.projectName,
    required this.category,
    required this.propertyType,
    required this.size,
    required this.unit,
    required this.sharePercent,
    required this.agreementId,
    required this.agreementNumber,
    required this.bookingNumber,
    required this.bookingStatus,
    required this.agreementStatus,
    required this.paymentMode,
    required this.bookingDate,
    required this.linePrice,
  });

  factory PortalProperty.fromJson(Map<String, dynamic> j) => PortalProperty(
        propertyId: J.integer(j['propertyId']),
        propertyCode: J.str(j['propertyCode']),
        propertyNumber: J.str(j['propertyNumber']),
        projectCode: J.str(j['projectCode']),
        projectName: J.str(j['projectName']),
        category: J.str(j['category']),
        propertyType: J.str(j['propertyType']),
        size: J.money(j['size']),
        unit: measurementUnits[J.integer(j['unit'])] ?? '',
        sharePercent: J.money(j['sharePercent']),
        agreementId: J.integer(j['agreementId']),
        agreementNumber: J.str(j['agreementNumber']),
        bookingNumber: J.str(j['bookingNumber']),
        bookingStatus: enumFrom(BookingStatus.values, (e) => e.code, j['bookingStatus'], BookingStatus.confirmed),
        agreementStatus: enumFrom(AgreementStatus.values, (e) => e.code, j['agreementStatus'], AgreementStatus.active),
        paymentMode: enumFrom(PaymentMode.values, (e) => e.code, j['paymentMode'], PaymentMode.installment),
        bookingDate: J.date(j['bookingDate']),
        linePrice: J.money(j['linePrice']),
      );

  final int propertyId;
  final String propertyCode;
  final String propertyNumber;
  final String projectCode;
  final String projectName;
  final String category;
  final String propertyType;
  final double size;
  final String unit;
  final double sharePercent;
  final int agreementId;
  final String agreementNumber;
  final String bookingNumber;
  final BookingStatus bookingStatus;
  final AgreementStatus agreementStatus;
  final PaymentMode paymentMode;
  final DateTime bookingDate;
  final double linePrice;
}

class AgreementProperty {
  AgreementProperty({required this.propertyCode, required this.propertyNumber, required this.size, required this.unit, required this.linePrice});

  factory AgreementProperty.fromJson(Map<String, dynamic> j) => AgreementProperty(
        propertyCode: J.str(j['propertyCode']),
        propertyNumber: J.str(j['propertyNumber']),
        size: J.money(j['size']),
        unit: measurementUnits[J.integer(j['unit'])] ?? '',
        linePrice: J.money(j['linePrice']),
      );

  final String propertyCode;
  final String propertyNumber;
  final double size;
  final String unit;
  final double linePrice;
}

class AgreementDetail {
  AgreementDetail({
    required this.agreementId,
    required this.agreementNumber,
    required this.status,
    required this.versionNo,
    required this.generatedAt,
    required this.bookingNumber,
    required this.bookingStatus,
    required this.bookingDate,
    required this.paymentMode,
    required this.projectCode,
    required this.projectName,
    required this.currencySymbol,
    required this.sharePercent,
    required this.isHolder,
    required this.properties,
    required this.summary,
    required this.agreementText,
  });

  factory AgreementDetail.fromJson(Map<String, dynamic> j) => AgreementDetail(
        agreementId: J.integer(j['agreementId']),
        agreementNumber: J.str(j['agreementNumber']),
        status: enumFrom(AgreementStatus.values, (e) => e.code, j['status'], AgreementStatus.active),
        versionNo: J.integer(j['versionNo']),
        generatedAt: J.date(j['generatedAtUtc']),
        bookingNumber: J.str(j['bookingNumber']),
        bookingStatus: enumFrom(BookingStatus.values, (e) => e.code, j['bookingStatus'], BookingStatus.confirmed),
        bookingDate: J.date(j['bookingDate']),
        paymentMode: enumFrom(PaymentMode.values, (e) => e.code, j['paymentMode'], PaymentMode.installment),
        projectCode: J.str(j['projectCode']),
        projectName: J.str(j['projectName']),
        currencySymbol: J.str(j['currencySymbol']),
        sharePercent: J.money(j['sharePercent']),
        isHolder: J.boolean(j['isHolder']),
        properties: J.list(j['properties']).map(AgreementProperty.fromJson).toList(),
        summary: AgreementSummary.fromJson(J.map(j['summary'])),
        agreementText: J.strOrNull(j['agreementText']),
      );

  final int agreementId;
  final String agreementNumber;
  final AgreementStatus status;
  final int versionNo;
  final DateTime generatedAt;
  final String bookingNumber;
  final BookingStatus bookingStatus;
  final DateTime bookingDate;
  final PaymentMode paymentMode;
  final String projectCode;
  final String projectName;
  final String currencySymbol;
  final double sharePercent;
  final bool isHolder;
  final List<AgreementProperty> properties;
  final AgreementSummary summary;
  final String? agreementText;
}
