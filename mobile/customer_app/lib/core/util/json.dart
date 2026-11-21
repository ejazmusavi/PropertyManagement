/// Small helpers to read the API's JSON (camelCase; enums are numbers; dates are ISO strings; decimals are numbers).
class J {
  static Map<String, dynamic> map(dynamic v) => (v as Map).cast<String, dynamic>();
  static List<Map<String, dynamic>> list(dynamic v) => (v as List? ?? const []).map((e) => (e as Map).cast<String, dynamic>()).toList();
  static double money(dynamic v) => (v as num? ?? 0).toDouble();
  static int integer(dynamic v) => (v as num? ?? 0).toInt();
  static String str(dynamic v, [String fallback = '']) => v?.toString() ?? fallback;
  static String? strOrNull(dynamic v) => v?.toString();
  static DateTime date(dynamic v) => DateTime.parse(v as String);
  static DateTime? dateOrNull(dynamic v) => v == null ? null : DateTime.parse(v as String);
  static bool boolean(dynamic v) => v == true;
}

/// Looks up an enum by the numeric code the API sends.
T enumFrom<T>(List<T> values, int Function(T) code, dynamic raw, T fallback) {
  final c = (raw as num?)?.toInt();
  for (final v in values) {
    if (code(v) == c) return v;
  }
  return fallback;
}
