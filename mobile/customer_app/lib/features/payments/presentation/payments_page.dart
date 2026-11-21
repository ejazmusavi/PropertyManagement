import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../models/payment_models.dart';
import 'payments_view_models.dart';

/// Payment history plus the entry points to proof submission, instructions and online payment.
class PaymentsPage extends StatelessWidget {
  const PaymentsPage({super.key, this.onlineEnabled = false});

  /// Whether the company enabled online payments (read from the dashboard).
  final bool onlineEnabled;

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<PaymentsViewModel>();
    return Scaffold(
      appBar: AppBar(title: const Text('Payments'), actions: [IconButton(tooltip: 'Payment instructions', onPressed: () => context.push('/payments/instructions'), icon: const Icon(Icons.account_balance_outlined))]),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () async {
          await context.push('/payments/submit');
          if (context.mounted) vm.load(refresh: true);
        },
        icon: const Icon(Icons.upload_file),
        label: const Text('Submit payment proof'),
      ),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: vm.items.isNotEmpty,
        onRetry: vm.load,
        builder: (_) => RefreshIndicator(
          onRefresh: () => vm.load(refresh: true),
          child: ListView(padding: const EdgeInsets.fromLTRB(16, 16, 16, 88), children: [
            if (onlineEnabled)
              Card(
                child: ListTile(
                  leading: const Icon(Icons.credit_card),
                  title: const Text('Pay online'),
                  subtitle: const Text('Pay an installment through the payment provider'),
                  trailing: const Icon(Icons.chevron_right),
                  onTap: () => context.push('/payments/online'),
                ),
              ),
            if (vm.items.isEmpty) const Padding(padding: EdgeInsets.symmetric(vertical: 48), child: EmptyView(icon: Icons.payments_outlined, message: 'No payments yet.\nSubmit a payment proof after you pay.')),
            for (final p in vm.items) _PaymentTile(p),
            if (vm.hasMore) Center(child: vm.loadingMore ? const Padding(padding: EdgeInsets.all(8), child: CircularProgressIndicator()) : OutlinedButton(onPressed: vm.loadMore, child: const Text('Load more'))),
          ]),
        ),
      ),
    );
  }
}

Color paymentStatusColor(PaymentStatus s) => switch (s) {
      PaymentStatus.posted => StatusColors.green,
      PaymentStatus.rejected || PaymentStatus.reversed => StatusColors.red,
      PaymentStatus.pendingApproval => StatusColors.blue,
      PaymentStatus.pendingVerification => StatusColors.amber,
    };

class _PaymentTile extends StatelessWidget {
  const _PaymentTile(this.p);
  final PortalPayment p;

  @override
  Widget build(BuildContext context) => Card(
        margin: const EdgeInsets.only(bottom: 8),
        child: ListTile(
          onTap: () => context.push('/payments/${p.id}'),
          title: Text(Fmt.money(p.amount, p.currencyCode), style: const TextStyle(fontWeight: FontWeight.w700)),
          subtitle: Text('${p.paymentNumber} · ${Fmt.date(p.paymentDate)}\n${p.methodName} · ${p.agreementNumber}'),
          isThreeLine: true,
          trailing: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.end, children: [
            StatusChip(p.status.label, paymentStatusColor(p.status)),
            if (p.hasReceipt) const Padding(padding: EdgeInsets.only(top: 4), child: Icon(Icons.receipt_long, size: 18)),
          ]),
        ),
      );
}
