/// Values sent to the API as text (multipart forms).
class FormatForApi {
  static String amount(double v) => v.toStringAsFixed(2);
  static String date(DateTime d) => '${d.year.toString().padLeft(4, '0')}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
}
