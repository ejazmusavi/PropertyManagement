import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../../properties/models/property_models.dart';
import '../models/payment_models.dart';
import 'payments_view_models.dart';

/// Online payment (shown only when the company enabled a provider). The provider's page opens in the browser; this screen follows the status.
class OnlinePaymentPage extends StatefulWidget {
  const OnlinePaymentPage({super.key, required this.agreements});
  final List<PortalProperty> agreements;

  @override
  State<OnlinePaymentPage> createState() => _OnlinePaymentPageState();
}

class _OnlinePaymentPageState extends State<OnlinePaymentPage> {
  final _form = GlobalKey<FormState>();
  final _amount = TextEditingController();
  int? _agreementId;

  @override
  void initState() {
    super.initState();
    final ids = widget.agreements.map((a) => a.agreementId).toSet();
    if (ids.length == 1) _agreementId = ids.first;
  }

  @override
  void dispose() {
    _amount.dispose();
    super.dispose();
  }

  Future<void> _start(OnlinePaymentViewModel vm) async {
    if (!_form.currentState!.validate()) return;
    if (!await vm.start(_agreementId!, Fmt.parseAmount(_amount.text)!)) return;
    final url = vm.payment?.redirectUrl;
    if (url != null) await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
    vm.startPolling();
  }

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<OnlinePaymentViewModel>();
    final p = vm.payment;
    final agreements = {for (final a in widget.agreements) a.agreementId: a.agreementNumber};
    return Scaffold(
      appBar: AppBar(title: const Text('Pay online')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        if (p == null)
          Form(
            key: _form,
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              if (vm.startError != null) Padding(padding: const EdgeInsets.only(bottom: 12), child: Text(vm.startError!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
              DropdownButtonFormField<int>(
                initialValue: _agreementId,
                decoration: const InputDecoration(labelText: 'Agreement', border: OutlineInputBorder()),
                items: [for (final e in agreements.entries) DropdownMenuItem(value: e.key, child: Text(e.value))],
                onChanged: (v) => setState(() => _agreementId = v),
                validator: (v) => v == null ? 'Choose the agreement' : null,
              ),
              const SizedBox(height: 16),
              TextFormField(
                controller: _amount,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(labelText: 'Amount to pay', border: OutlineInputBorder()),
                validator: (v) => Fmt.parseAmount(v ?? '') == null ? 'Enter an amount greater than zero (at most two decimals)' : null,
              ),
              const SizedBox(height: 16),
              FilledButton(onPressed: vm.starting ? null : () => _start(vm), child: vm.starting ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2)) : const Text('Continue to payment')),
            ]),
          )
        else
          SectionCard(title: p.transactionNumber, child: Column(children: [
            InfoRow('Amount', Fmt.money(p.amount, p.currencyCode)),
            InfoRow('Status', p.status.label, bold: true, color: p.status == GatewayStatus.succeeded ? StatusColors.green : (p.status == GatewayStatus.failed ? StatusColors.red : null)),
            if (p.failureReason != null) InfoRow('Reason', p.failureReason!),
            if (p.paymentNumber != null) InfoRow('Payment', p.paymentNumber!),
            const SizedBox(height: 8),
            if (!p.status.isFinal) ...[
              const LinearProgressIndicator(),
              const SizedBox(height: 8),
              const Text('Complete the payment in the browser. This screen updates by itself.'),
              Row(children: [
                if (p.redirectUrl != null) TextButton(onPressed: () => launchUrl(Uri.parse(p.redirectUrl!), mode: LaunchMode.externalApplication), child: const Text('Open payment page')),
                TextButton(onPressed: vm.refreshStatus, child: const Text('Check status')),
              ]),
            ],
            if (p.status == GatewayStatus.succeeded) const Text('Your payment was confirmed. The office approves it and your receipt then appears under Payments.'),
          ])),
      ]),
    );
  }
}
