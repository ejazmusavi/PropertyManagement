import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../models/installment_models.dart';
import 'installments_view_model.dart';

class InstallmentsPage extends StatelessWidget {
  const InstallmentsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<InstallmentsViewModel>();
    final s = vm.schedule;
    return Scaffold(
      appBar: AppBar(title: Text(s == null ? 'Installments' : 'Installments · ${s.agreementNumber}')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: s != null,
        onRetry: vm.load,
        builder: (_) {
          final c = s!.currencySymbol;
          final sum = s.summary;
          final items = vm.visible;
          return RefreshIndicator(
            onRefresh: () => vm.load(refresh: true),
            child: ListView(padding: const EdgeInsets.all(16), children: [
              SectionCard(title: 'Payment schedule', child: Column(children: [
                InfoRow('Total scheduled', Fmt.money(sum.totalAmount, c)),
                InfoRow('Paid (${sum.paidCount} installments)', Fmt.money(sum.paid, c), color: StatusColors.green),
                InfoRow('Outstanding (${sum.outstandingCount})', Fmt.money(sum.outstanding, c), bold: true),
                InfoRow('Due now', Fmt.money(sum.outstandingDue, c)),
                InfoRow('Overdue (${sum.overdueCount})', Fmt.money(sum.overdue, c), color: sum.overdue > 0 ? StatusColors.red : null),
                InfoRow('Not yet due', Fmt.money(sum.notYetDue, c)),
              ])),
              Wrap(spacing: 8, children: [
                for (final f in InstallmentFilter.values)
                  ChoiceChip(
                    label: Text(switch (f) {
                      InstallmentFilter.all => 'All',
                      InstallmentFilter.outstanding => 'Outstanding',
                      InstallmentFilter.overdue => 'Overdue',
                      InstallmentFilter.paid => 'Paid',
                    }),
                    selected: vm.filter == f,
                    onSelected: (_) => vm.setFilter(f),
                  ),
              ]),
              const SizedBox(height: 8),
              if (items.isEmpty) const Padding(padding: EdgeInsets.all(24), child: Center(child: Text('Nothing to show for this filter.'))),
              for (final i in items) _InstallmentTile(i, c),
            ]),
          );
        },
      ),
    );
  }
}

class _InstallmentTile extends StatelessWidget {
  const _InstallmentTile(this.i, this.currency);
  final Installment i;
  final String currency;

  Color get _color => switch (i.status) {
        InstallmentStatus.paid || InstallmentStatus.waived => StatusColors.green,
        InstallmentStatus.overdue => StatusColors.red,
        InstallmentStatus.partiallyPaid => StatusColors.amber,
        InstallmentStatus.upcoming => StatusColors.blue,
      };

  @override
  Widget build(BuildContext context) => Card(
        margin: const EdgeInsets.only(bottom: 8),
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Expanded(child: Text(i.description, style: Theme.of(context).textTheme.titleSmall)),
              StatusChip(i.status.label, _color),
            ]),
            const SizedBox(height: 4),
            Text('Due ${Fmt.date(i.dueDate)}${i.installmentCount > 1 ? ' · ${i.installmentNo} of ${i.installmentCount}' : ''}', style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 8),
            InfoRow('Amount', Fmt.money(i.amount, currency)),
            if (i.paid > 0) InfoRow('Paid', Fmt.money(i.paid, currency), color: StatusColors.green),
            if (i.waived > 0) InfoRow('Waived', Fmt.money(i.waived, currency)),
            if (i.outstanding > 0) InfoRow('Outstanding', Fmt.money(i.outstanding, currency), bold: true, color: i.status == InstallmentStatus.overdue ? StatusColors.red : null),
            if (i.lastPaymentDate != null) InfoRow('Last payment', Fmt.date(i.lastPaymentDate!)),
          ]),
        ),
      );
}
