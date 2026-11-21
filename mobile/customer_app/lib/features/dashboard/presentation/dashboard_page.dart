import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../models/dashboard.dart';
import 'dashboard_view_model.dart';

class DashboardPage extends StatelessWidget {
  const DashboardPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<DashboardViewModel>();
    final d = vm.data;
    return Scaffold(
      appBar: AppBar(
        title: const Text('Home'),
        actions: [
          IconButton(
            tooltip: 'Notifications',
            onPressed: () => context.push('/more/notifications'),
            icon: Badge(isLabelVisible: (d?.unreadNotifications ?? 0) > 0, label: Text('${d?.unreadNotifications ?? 0}'), child: const Icon(Icons.notifications_outlined)),
          ),
        ],
      ),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: d != null,
        onRetry: vm.load,
        builder: (_) => RefreshIndicator(
          onRefresh: () => vm.load(refresh: true),
          child: ListView(padding: const EdgeInsets.all(16), children: [
            Text('Welcome, ${d!.customerName}', style: Theme.of(context).textTheme.titleLarge),
            Text(d.customerCode, style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 12),
            _Totals(d),
            if (d.overdueAmount > 0) _OverdueBanner(d),
            _Upcoming(d),
            if (d.activeAgreements == 0)
              const SectionCard(child: Text('You have no active agreements yet. Your properties will appear here once the office confirms your booking.')),
          ]),
        ),
      ),
    );
  }
}

class _Totals extends StatelessWidget {
  const _Totals(this.d);
  final Dashboard d;

  @override
  Widget build(BuildContext context) {
    final s = d.currencySymbol;
    return Column(children: [
      Row(children: [
        Expanded(child: _Metric('Properties', '${d.totalProperties}', Icons.home_work_outlined)),
        const SizedBox(width: 8),
        Expanded(child: _Metric('Agreement value', Fmt.money(d.totalAgreementValue, s), Icons.description_outlined)),
      ]),
      const SizedBox(height: 8),
      Row(children: [
        Expanded(child: _Metric('Paid', Fmt.money(d.paidAmount, s), Icons.check_circle_outline, color: StatusColors.green)),
        const SizedBox(width: 8),
        Expanded(child: _Metric('Outstanding', Fmt.money(d.outstandingAmount, s), Icons.pending_actions_outlined, color: d.outstandingAmount > 0 ? StatusColors.amber : null)),
      ]),
    ]);
  }
}

class _Metric extends StatelessWidget {
  const _Metric(this.label, this.value, this.icon, {this.color});
  final String label;
  final String value;
  final IconData icon;
  final Color? color;

  @override
  Widget build(BuildContext context) => Card(
        margin: EdgeInsets.zero,
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [Icon(icon, size: 18, color: color), const SizedBox(width: 6), Expanded(child: Text(label, style: Theme.of(context).textTheme.labelMedium))]),
            const SizedBox(height: 6),
            FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(value, style: Theme.of(context).textTheme.titleMedium?.copyWith(color: color, fontWeight: FontWeight.w700))),
          ]),
        ),
      );
}

class _OverdueBanner extends StatelessWidget {
  const _OverdueBanner(this.d);
  final Dashboard d;

  @override
  Widget build(BuildContext context) => Card(
        color: Theme.of(context).colorScheme.errorContainer,
        margin: const EdgeInsets.only(top: 12),
        child: ListTile(
          leading: Icon(Icons.warning_amber_rounded, color: Theme.of(context).colorScheme.onErrorContainer),
          title: Text('${Fmt.money(d.overdueAmount, d.currencySymbol)} is overdue', style: TextStyle(color: Theme.of(context).colorScheme.onErrorContainer, fontWeight: FontWeight.w600)),
          subtitle: Text('Please pay as soon as possible.', style: TextStyle(color: Theme.of(context).colorScheme.onErrorContainer)),
        ),
      );
}

class _Upcoming extends StatelessWidget {
  const _Upcoming(this.d);
  final Dashboard d;

  @override
  Widget build(BuildContext context) {
    if (d.upcomingDues.isEmpty) return const SizedBox.shrink();
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: SectionCard(
        title: 'Upcoming dues',
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
        child: Column(children: [
          for (final due in d.upcomingDues)
            ListTile(
              contentPadding: EdgeInsets.zero,
              dense: true,
              onTap: () => context.push('/agreement/${due.agreementId}/installments'),
              title: Text(due.description),
              subtitle: Text('${due.agreementNumber} · due ${Fmt.date(due.dueDate)}'),
              trailing: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.end, children: [
                Text(Fmt.money(due.outstanding, d.currencySymbol), style: const TextStyle(fontWeight: FontWeight.w600)),
                if (due.isOverdue) const StatusChip('Overdue', StatusColors.red),
              ]),
            ),
        ]),
      ),
    );
  }
}
