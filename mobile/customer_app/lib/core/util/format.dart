import 'package:intl/intl.dart';

/// Display formatting. Amounts are only displayed here; every figure is computed by the backend.
class Fmt {
  static final NumberFormat _money = NumberFormat('#,##0.00');
  static final DateFormat _date = DateFormat('dd MMM yyyy');
  static final DateFormat _dateTime = DateFormat('dd MMM yyyy, HH:mm');

  static String money(num value, [String symbol = '']) => symbol.isEmpty ? _money.format(value) : '$symbol ${_money.format(value)}';
  static String date(DateTime d) => _date.format(d);
  static String dateTime(DateTime d) => _dateTime.format(d.toLocal());
  static String isoDate(DateTime d) => DateFormat('yyyy-MM-dd').format(d);
  static String size(num size, String unit) => '${NumberFormat('#,##0.##').format(size)} $unit';

  /// Parses a user-typed amount ("1,500.5") into a value with at most two decimals, or null.
  static double? parseAmount(String text) {
    final cleaned = text.replaceAll(',', '').trim();
    final v = double.tryParse(cleaned);
    if (v == null || v <= 0) return null;
    final rounded = (v * 100).round() / 100;
    return (rounded - v).abs() < 1e-9 ? rounded : null;
  }
}
