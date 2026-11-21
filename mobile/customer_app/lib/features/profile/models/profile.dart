import '../../../core/util/json.dart';

class Profile {
  Profile({required this.customerCode, required this.fullName, required this.identityNumberMasked, required this.mobile, required this.email, required this.address, required this.loginEmail});

  factory Profile.fromJson(Map<String, dynamic> j) => Profile(
        customerCode: J.str(j['customerCode']),
        fullName: J.str(j['fullName']),
        identityNumberMasked: J.str(j['identityNumberMasked']),
        mobile: J.str(j['mobile']),
        email: J.strOrNull(j['email']),
        address: J.strOrNull(j['address']),
        loginEmail: J.str(j['loginEmail']),
      );

  final String customerCode;
  final String fullName;
  final String identityNumberMasked;
  final String mobile;
  final String? email;
  final String? address;
  final String loginEmail;
}
