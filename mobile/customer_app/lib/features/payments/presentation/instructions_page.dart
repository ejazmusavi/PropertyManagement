import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../../../core/widgets/common.dart';
import 'payments_view_models.dart';

class InstructionsPage extends StatelessWidget {
  const InstructionsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<InstructionsViewModel>();
    final i = vm.instructions;
    return Scaffold(
      appBar: AppBar(title: const Text('How to pay')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: i != null,
        onRetry: vm.load,
        builder: (_) => ListView(padding: const EdgeInsets.all(16), children: [
          if (i!.note != null && i.note!.isNotEmpty) SectionCard(title: 'Instructions', child: Text(i.note!)),
          if (i.bankAccounts.isEmpty && (i.note == null || i.note!.isEmpty))
            const SectionCard(child: Text('Payment details are not published yet. Please contact the office.')),
          for (final b in i.bankAccounts)
            SectionCard(title: b.bankName, child: Column(children: [
              if (b.branch != null) InfoRow('Branch', b.branch!),
              InfoRow('Account title', b.accountTitle),
              InfoRow('Account number', b.accountNumber),
              if (b.iban != null) InfoRow('IBAN', b.iban!),
              Align(
                alignment: Alignment.centerRight,
                child: TextButton.icon(
                  onPressed: () {
                    Clipboard.setData(ClipboardData(text: b.iban ?? b.accountNumber));
                    showMessage(context, 'Copied');
                  },
                  icon: const Icon(Icons.copy, size: 16),
                  label: Text(b.iban != null ? 'Copy IBAN' : 'Copy account number'),
                ),
              ),
            ])),
          const SectionCard(child: Text('After you pay, use “Submit payment proof” on the Payments tab to send the slip. The office verifies it and your receipt appears in the app.')),
        ]),
      ),
    );
  }
}
