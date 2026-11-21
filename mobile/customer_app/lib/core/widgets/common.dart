import 'package:flutter/material.dart';

import '../util/view_model.dart';

/// Loading / error / retry wrapper used by every list and detail screen.
class AsyncBody extends StatelessWidget {
  const AsyncBody({super.key, required this.status, required this.error, required this.onRetry, required this.builder, this.hasData = false});

  final LoadStatus status;
  final String? error;
  final VoidCallback onRetry;
  final WidgetBuilder builder;

  /// When true the data is shown even while a refresh is running or after a refresh failed.
  final bool hasData;

  @override
  Widget build(BuildContext context) {
    if (hasData) return builder(context);
    switch (status) {
      case LoadStatus.idle:
      case LoadStatus.loading:
        return const Center(child: CircularProgressIndicator());
      case LoadStatus.error:
        return ErrorView(message: error ?? 'Something went wrong.', onRetry: onRetry);
      case LoadStatus.loaded:
        return builder(context);
    }
  }
}

class ErrorView extends StatelessWidget {
  const ErrorView({super.key, required this.message, required this.onRetry});
  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            const Icon(Icons.cloud_off_outlined, size: 48),
            const SizedBox(height: 12),
            Text(message, textAlign: TextAlign.center),
            const SizedBox(height: 16),
            FilledButton.icon(onPressed: onRetry, icon: const Icon(Icons.refresh), label: const Text('Try again')),
          ]),
        ),
      );
}

class EmptyView extends StatelessWidget {
  const EmptyView({super.key, required this.icon, required this.message});
  final IconData icon;
  final String message;

  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Icon(icon, size: 48, color: Theme.of(context).colorScheme.outline),
            const SizedBox(height: 12),
            Text(message, textAlign: TextAlign.center, style: Theme.of(context).textTheme.bodyLarge),
          ]),
        ),
      );
}

/// A label with a value on one line (amounts are right aligned).
class InfoRow extends StatelessWidget {
  const InfoRow(this.label, this.value, {super.key, this.bold = false, this.color});
  final String label;
  final String value;
  final bool bold;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final style = Theme.of(context).textTheme.bodyMedium?.copyWith(fontWeight: bold ? FontWeight.w700 : null, color: color);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Expanded(flex: 4, child: Text(label, style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: Theme.of(context).colorScheme.onSurfaceVariant))),
        Expanded(flex: 5, child: Text(value, textAlign: TextAlign.end, style: style)),
      ]),
    );
  }
}

class SectionCard extends StatelessWidget {
  const SectionCard({super.key, this.title, required this.child, this.padding = const EdgeInsets.all(16)});
  final String? title;
  final Widget child;
  final EdgeInsets padding;

  @override
  Widget build(BuildContext context) => Card(
        margin: const EdgeInsets.only(bottom: 12),
        child: Padding(
          padding: padding,
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            if (title != null) ...[Text(title!, style: Theme.of(context).textTheme.titleMedium), const SizedBox(height: 8)],
            child,
          ]),
        ),
      );
}

class StatusChip extends StatelessWidget {
  const StatusChip(this.label, this.color, {super.key});
  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
        decoration: BoxDecoration(color: color.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(12), border: Border.all(color: color.withValues(alpha: 0.5))),
        child: Text(label, style: TextStyle(color: color, fontSize: 12, fontWeight: FontWeight.w600)),
      );
}

void showMessage(BuildContext context, String message, {bool error = false}) {
  final messenger = ScaffoldMessenger.of(context);
  messenger
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(message), backgroundColor: error ? Theme.of(context).colorScheme.error : null));
}

class StatusColors {
  static const green = Color(0xFF2E7D32);
  static const red = Color(0xFFC62828);
  static const amber = Color(0xFFEF6C00);
  static const blue = Color(0xFF1565C0);
  static const grey = Color(0xFF616161);
}
