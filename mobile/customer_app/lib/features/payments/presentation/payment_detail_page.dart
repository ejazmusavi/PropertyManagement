import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import 'payments_page.dart';
import 'payments_view_models.dart';

class PaymentDetailPage extends StatelessWidget {
  const PaymentDetailPage({super.key});

  Future<void> _run(BuildContext context, Future<bool> Function() action) async {
    final vm = context.read<PaymentDetailViewModel>();
    final ok = await action();
    if (!ok && context.mounted && vm.downloadError != null) showMessage(context, vm.downloadError!, error: true);
  }

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<PaymentDetailViewModel>();
    final d = vm.detail;
    return Scaffold(
      appBar: AppBar(title: Text(d?.payment.paymentNumber ?? 'Payment')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: d != null,
        onRetry: vm.load,
        builder: (_) {
          final p = d!.payment;
          return RefreshIndicator(
            onRefresh: vm.load,
            child: ListView(padding: const EdgeInsets.all(16), children: [
              SectionCard(child: Column(children: [
                Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
                  Expanded(child: FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(Fmt.money(p.amount, p.currencyCode), style: Theme.of(context).textTheme.headlineSmall))),
                  const SizedBox(width: 8),
                  StatusChip(p.status.label, paymentStatusColor(p.status)),
                ]),
                const SizedBox(height: 8),
                InfoRow('Payment date', Fmt.date(p.paymentDate)),
                InfoRow('Method', p.methodName),
                if (p.reference != null) InfoRow('Reference', p.reference!),
                InfoRow('Agreement', p.agreementNumber),
                InfoRow('Submitted', Fmt.dateTime(p.submittedAt)),
                if (p.customerSubmitted) const InfoRow('Source', 'Submitted by you'),
                if (d.note != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text(d.note!, style: Theme.of(context).textTheme.bodySmall)),
                if (p.rejectReason != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text('Reason: ${p.rejectReason}', style: const TextStyle(color: StatusColors.red))),
              ])),
              if (p.hasReceipt)
                FilledButton.icon(
                  key: const Key('download-receipt'),
                  onPressed: vm.downloading ? null : () => _run(context, vm.openReceipt),
                  icon: vm.downloading ? const SizedBox(height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2)) : const Icon(Icons.picture_as_pdf),
                  label: Text('Download receipt ${p.receiptNumber ?? ''}'),
                ),
              if (d.allocations.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: SectionCard(title: 'Applied to', child: Column(children: [
                    for (final a in d.allocations) InfoRow(a.description, Fmt.money(a.amount, d.currencySymbol)),
                    if (d.unallocated > 0) InfoRow('Not yet applied', Fmt.money(d.unallocated, d.currencySymbol)),
                  ])),
                ),
              if (d.proofs.isNotEmpty)
                SectionCard(title: 'Your payment proof', padding: const EdgeInsets.fromLTRB(16, 16, 16, 8), child: Column(children: [
                  for (final f in d.proofs)
                    ListTile(
                      contentPadding: EdgeInsets.zero,
                      dense: true,
                      leading: const Icon(Icons.attach_file),
                      title: Text(f.fileName),
                      subtitle: Text('${(f.sizeBytes / 1024).ceil()} KB · ${Fmt.dateTime(f.uploadedAt)}'),
                      onTap: vm.downloading ? null : () => _run(context, () => vm.openProof(f)),
                    ),
                ])),
            ]),
          );
        },
      ),
    );
  }
}
