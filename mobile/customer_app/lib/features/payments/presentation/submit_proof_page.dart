import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/network/api_client.dart';
import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../../properties/models/property_models.dart';
import '../models/payment_models.dart';
import 'payments_view_models.dart';

/// Payment proof submission: the customer states what they paid and attaches the slip. Nothing is credited until the office verifies it.
class SubmitProofPage extends StatefulWidget {
  const SubmitProofPage({super.key, required this.agreements, this.initialAgreementId});

  /// The customer's own agreements (loaded from the properties list; ids are never typed by the user).
  final List<PortalProperty> agreements;
  final int? initialAgreementId;

  @override
  State<SubmitProofPage> createState() => _SubmitProofPageState();
}

class _SubmitProofPageState extends State<SubmitProofPage> {
  final _form = GlobalKey<FormState>();
  final _amount = TextEditingController();
  final _reference = TextEditingController();
  final _bank = TextEditingController();
  final _remarks = TextEditingController();
  int? _agreementId;
  PaymentMethod? _method;
  DateTime _date = DateTime.now();
  UploadFile? _file;
  String? _fileError;

  @override
  void initState() {
    super.initState();
    final ids = widget.agreements.map((a) => a.agreementId).toSet();
    _agreementId = widget.initialAgreementId ?? (ids.length == 1 ? ids.first : null);
  }

  @override
  void dispose() {
    for (final c in [_amount, _reference, _bank, _remarks]) {
      c.dispose();
    }
    super.dispose();
  }

  Future<void> _pick() async {
    final f = await FilePicker.pickFile(type: FileType.custom, allowedExtensions: const ['pdf', 'jpg', 'jpeg', 'png']);
    if (f == null) return;
    final bytes = await f.readAsBytes();
    final ext = (f.extension ?? '').toLowerCase();
    setState(() {
      _file = UploadFile(f.name, bytes, ext == 'pdf' ? 'application/pdf' : (ext == 'png' ? 'image/png' : 'image/jpeg'));
      _fileError = null;
    });
  }

  Future<void> _pickDate() async {
    final d = await showDatePicker(context: context, initialDate: _date, firstDate: DateTime.now().subtract(const Duration(days: 365)), lastDate: DateTime.now());
    if (d != null) setState(() => _date = d);
  }

  Future<void> _submit(SubmitProofViewModel vm) async {
    final valid = _form.currentState!.validate();
    if (_file == null) setState(() => _fileError = 'Attach the bank slip or a screenshot of the payment.');
    if (!valid || _file == null) return;
    final ok = await vm.submit(
      agreementId: _agreementId!,
      methodId: _method!.id,
      amount: Fmt.parseAmount(_amount.text)!,
      date: _date,
      reference: _reference.text.trim(),
      bankName: _bank.text.trim(),
      remarks: _remarks.text.trim(),
      file: _file!,
    );
    if (ok && mounted) {
      showMessage(context, 'Payment proof sent. The office will verify it.');
      context.pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<SubmitProofViewModel>();
    final methods = vm.instructions?.methods ?? const <PaymentMethod>[];
    final agreements = {for (final a in widget.agreements) a.agreementId: a.agreementNumber};
    return Scaffold(
      appBar: AppBar(title: const Text('Submit payment proof')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: vm.instructions != null,
        onRetry: vm.load,
        builder: (_) => SingleChildScrollView(
          padding: const EdgeInsets.all(16),
          child: Form(
            key: _form,
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              if (vm.submitError != null) Padding(padding: const EdgeInsets.only(bottom: 12), child: Text(vm.submitError!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
              DropdownButtonFormField<int>(
                key: const Key('proof-agreement'),
                initialValue: _agreementId,
                decoration: const InputDecoration(labelText: 'Agreement', border: OutlineInputBorder()),
                items: [for (final e in agreements.entries) DropdownMenuItem(value: e.key, child: Text(e.value))],
                onChanged: (v) => setState(() => _agreementId = v),
                validator: (v) => v == null ? 'Choose the agreement you paid for' : null,
              ),
              const SizedBox(height: 16),
              DropdownButtonFormField<PaymentMethod>(
                key: const Key('proof-method'),
                initialValue: _method,
                decoration: const InputDecoration(labelText: 'How did you pay?', border: OutlineInputBorder()),
                items: [for (final m in methods) DropdownMenuItem(value: m, child: Text(m.name))],
                onChanged: (v) => setState(() => _method = v),
                validator: (v) => v == null ? 'Choose the payment method' : null,
              ),
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('proof-amount'),
                controller: _amount,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(labelText: 'Amount paid', border: OutlineInputBorder()),
                validator: (v) => Fmt.parseAmount(v ?? '') == null ? 'Enter an amount greater than zero (at most two decimals)' : null,
              ),
              const SizedBox(height: 16),
              InkWell(
                onTap: _pickDate,
                child: InputDecorator(decoration: const InputDecoration(labelText: 'Payment date', border: OutlineInputBorder(), suffixIcon: Icon(Icons.calendar_today)), child: Text(Fmt.date(_date))),
              ),
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('proof-reference'),
                controller: _reference,
                decoration: const InputDecoration(labelText: 'Transaction / slip number', border: OutlineInputBorder()),
                validator: (v) => (_method?.requiresReference ?? false) && (v == null || v.trim().isEmpty) ? 'Enter the transaction or slip number' : null,
              ),
              const SizedBox(height: 16),
              TextFormField(controller: _bank, decoration: const InputDecoration(labelText: 'Bank (optional)', border: OutlineInputBorder())),
              const SizedBox(height: 16),
              TextFormField(controller: _remarks, maxLines: 2, decoration: const InputDecoration(labelText: 'Remarks (optional)', border: OutlineInputBorder())),
              const SizedBox(height: 16),
              OutlinedButton.icon(key: const Key('proof-pick'), onPressed: _pick, icon: const Icon(Icons.attach_file), label: Text(_file == null ? 'Attach slip or screenshot' : _file!.fileName)),
              if (_fileError != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(_fileError!, style: TextStyle(color: Theme.of(context).colorScheme.error, fontSize: 12))),
              const SizedBox(height: 20),
              FilledButton(
                key: const Key('proof-submit'),
                onPressed: vm.submitting ? null : () => _submit(vm),
                child: vm.submitting ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2)) : const Text('Send payment proof'),
              ),
            ]),
          ),
        ),
      ),
    );
  }
}
