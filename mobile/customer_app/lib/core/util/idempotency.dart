import 'dart:math';

/// A fresh random key for a submission. One key is created per form instance and reused when the customer retries,
/// so a lost response never creates a second payment.
String newIdempotencyKey() {
  final r = Random.secure();
  final bytes = List<int>.generate(16, (_) => r.nextInt(256));
  return bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
}
