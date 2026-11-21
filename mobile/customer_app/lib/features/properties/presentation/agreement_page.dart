import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import 'properties_view_models.dart';

/// Agreement details: booking, properties, amounts and the shortcuts to the schedule and the statement.
class AgreementPage extends StatelessWidget {
  const AgreementPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<AgreementViewModel>();
    final a = vm.agreement;
    return Scaffold(
      appBar: AppBar(title: Text(a?.agreementNumber ?? 'Agreement')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: a != null,
        onRetry: vm.load,
        builder: (_) {
          final s = a!.summary;
          final c = a.currencySymbol;
          return ListView(padding: const EdgeInsets.all(16), children: [
            SectionCard(title: 'Agreement', child: Column(children: [
              InfoRow('Agreement number', a.agreementNumber),
              InfoRow('Status', a.status.label),
              InfoRow('Version', '${a.versionNo} (${Fmt.date(a.generatedAt)})'),
              InfoRow('Booking', '${a.bookingNumber} · ${a.bookingStatus.label}'),
              InfoRow('Booking date', Fmt.date(a.bookingDate)),
              InfoRow('Project', '${a.projectName} (${a.projectCode})'),
              InfoRow('Payment plan', a.paymentMode.label),
              if (a.sharePercent < 100) InfoRow('Your share', '${a.sharePercent.toStringAsFixed(2)} %'),
            ])),
            SectionCard(title: a.properties.length == 1 ? 'Property' : 'Properties', child: Column(children: [
              for (final p in a.properties) ...[
                InfoRow('${p.propertyCode} (No. ${p.propertyNumber})', Fmt.money(p.linePrice, c)),
                Align(alignment: Alignment.centerLeft, child: Text(Fmt.size(p.size, p.unit), style: Theme.of(context).textTheme.bodySmall)),
              ],
            ])),
            SectionCard(title: 'Amounts', child: Column(children: [
              InfoRow('Agreement value', Fmt.money(s.agreementValue, c)),
              if (s.discounts != 0) InfoRow('Discounts', Fmt.money(s.discounts, c)),
              if (s.charges != 0) InfoRow('Charges', Fmt.money(s.charges, c)),
              InfoRow('Total payable', Fmt.money(s.contractTotal, c), bold: true),
              const Divider(),
              InfoRow('Paid', Fmt.money(s.paid, c), color: StatusColors.green),
              InfoRow('Due now', Fmt.money(s.outstanding, c), bold: s.outstanding > 0),
              InfoRow('Overdue', Fmt.money(s.overdue, c), color: s.overdue > 0 ? StatusColors.red : null),
              InfoRow('Not yet due', Fmt.money(s.notYetDue, c)),
              if (s.advanceBalance + s.unallocatedBalance > 0) InfoRow('Advance paid', Fmt.money(s.advanceBalance + s.unallocatedBalance, c)),
            ])),
            Row(children: [
              Expanded(child: FilledButton.icon(onPressed: () => context.push('/agreement/${a.agreementId}/installments'), icon: const Icon(Icons.event_note_outlined), label: const Text('Installments'))),
              const SizedBox(width: 8),
              Expanded(child: OutlinedButton.icon(onPressed: () => context.push('/agreement/${a.agreementId}/statement'), icon: const Icon(Icons.receipt_long_outlined), label: const Text('Statement'))),
            ]),
            if (a.agreementText != null && a.agreementText!.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 12),
                child: SectionCard(
                  title: 'Agreement text',
                  child: ExpansionTile(
                    tilePadding: EdgeInsets.zero,
                    title: const Text('Read the agreement'),
                    children: [SelectableText(a.agreementText!, style: Theme.of(context).textTheme.bodySmall)],
                  ),
                ),
              ),
          ]);
        },
      ),
    );
  }
}
