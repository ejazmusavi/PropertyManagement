import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../models/ledger_models.dart';
import 'ledger_view_models.dart';

/// Customer statement: balances of all agreements and the transaction history.
class LedgerPage extends StatelessWidget {
  const LedgerPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<LedgerViewModel>();
    final l = vm.ledger;
    return Scaffold(
      appBar: AppBar(title: const Text('My statement')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: l != null,
        onRetry: vm.load,
        builder: (_) {
          final c = l!.currencySymbol;
          return RefreshIndicator(
            onRefresh: () => vm.load(refresh: true),
            child: ListView(padding: const EdgeInsets.all(16), children: [
              SectionCard(title: 'Balance', child: Column(children: [
                InfoRow('Outstanding balance', Fmt.money(l.balance, c), bold: true, color: l.balance > 0 ? StatusColors.amber : null),
                InfoRow('Due now', Fmt.money(l.outstanding, c)),
                InfoRow('Overdue', Fmt.money(l.overdue, c), color: l.overdue > 0 ? StatusColors.red : null),
                InfoRow('Advance paid', Fmt.money(l.advanceBalance, c)),
                const Divider(),
                InfoRow('Total charged', Fmt.money(l.totalDebit, c)),
                InfoRow('Total paid / credited', Fmt.money(l.totalCredit, c)),
              ])),
              if (l.agreements.isNotEmpty)
                SectionCard(title: 'Agreements', padding: const EdgeInsets.fromLTRB(16, 16, 16, 8), child: Column(children: [
                  for (final a in l.agreements)
                    ListTile(
                      contentPadding: EdgeInsets.zero,
                      dense: true,
                      onTap: a.isHolder ? () => context.push('/agreement/${a.agreementId}/statement') : null,
                      title: Text(a.agreementNumber),
                      subtitle: Text(a.projectCode),
                      trailing: Text(Fmt.money(a.balance, c), style: const TextStyle(fontWeight: FontWeight.w600)),
                    ),
                ])),
              Text('Transactions', style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 8),
              if (vm.entries.isEmpty) const Padding(padding: EdgeInsets.all(24), child: Center(child: Text('No transactions yet.'))),
              for (final e in vm.entries) EntryTile(e, c),
              if (vm.hasMore)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 8),
                  child: Center(child: vm.loadingMore ? const CircularProgressIndicator() : OutlinedButton(onPressed: vm.loadMore, child: const Text('Load more'))),
                ),
            ]),
          );
        },
      ),
    );
  }
}

class AgreementLedgerPage extends StatelessWidget {
  const AgreementLedgerPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<AgreementLedgerViewModel>();
    final l = vm.ledger;
    return Scaffold(
      appBar: AppBar(title: Text(l == null ? 'Agreement statement' : 'Statement · ${l.agreementNumber}')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: l != null,
        onRetry: vm.load,
        builder: (_) {
          final s = l!.summary;
          final c = l.currencySymbol;
          return ListView(padding: const EdgeInsets.all(16), children: [
            SectionCard(title: l.properties.join(', '), child: Column(children: [
              InfoRow('Contract total', Fmt.money(s.contractTotal, c)),
              InfoRow('Payments received', Fmt.money(s.paymentsReceived, c), color: StatusColors.green),
              if (s.paymentsReversed > 0) InfoRow('Payments reversed', Fmt.money(s.paymentsReversed, c)),
              if (s.waivers > 0) InfoRow('Waivers', Fmt.money(s.waivers, c)),
              if (s.refunds > 0) InfoRow('Refunds', Fmt.money(s.refunds, c)),
              InfoRow('Outstanding (due)', Fmt.money(s.outstanding, c), bold: true),
              InfoRow('Overdue', Fmt.money(s.overdue, c), color: s.overdue > 0 ? StatusColors.red : null),
              InfoRow('Not yet due', Fmt.money(s.notYetDue, c)),
              if (s.penaltiesAccrued > 0) InfoRow('Late fees accrued', Fmt.money(s.penaltiesAccrued, c)),
              InfoRow('Ledger balance', Fmt.money(s.ledgerBalance, c), bold: true),
            ])),
            Text('Transactions', style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 8),
            if (vm.entries.isEmpty) const Padding(padding: EdgeInsets.all(24), child: Center(child: Text('No transactions yet.'))),
            for (final e in vm.entries) EntryTile(e, c),
            if (vm.hasMore)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Center(child: vm.loadingMore ? const CircularProgressIndicator() : OutlinedButton(onPressed: vm.loadMore, child: const Text('Load more'))),
              ),
          ]);
        },
      ),
    );
  }
}

class EntryTile extends StatelessWidget {
  const EntryTile(this.e, this.currency, {super.key});
  final LedgerEntry e;
  final String currency;

  @override
  Widget build(BuildContext context) {
    final isCredit = e.credit > 0;
    final amount = isCredit ? e.credit : e.debit;
    return Card(
      margin: const EdgeInsets.only(bottom: 6),
      child: ListTile(
        dense: true,
        title: Text(e.description, maxLines: 2, overflow: TextOverflow.ellipsis),
        subtitle: Text('${Fmt.date(e.entryDate)} · ${e.type.label}${e.agreementNumber == null ? '' : ' · ${e.agreementNumber}'}'),
        trailing: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.end, children: [
          Text('${isCredit ? '−' : '+'} ${Fmt.money(amount, currency)}', style: TextStyle(fontWeight: FontWeight.w600, color: isCredit ? StatusColors.green : null)),
          Text('Balance ${Fmt.money(e.balance, currency)}', style: Theme.of(context).textTheme.bodySmall),
        ]),
      ),
    );
  }
}
