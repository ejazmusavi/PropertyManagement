import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../../properties/models/property_models.dart';
import '../models/support_models.dart';
import 'support_view_models.dart';

Color supportStatusColor(SupportStatus s) => switch (s) {
      SupportStatus.open => StatusColors.amber,
      SupportStatus.inProgress => StatusColors.blue,
      SupportStatus.resolved => StatusColors.green,
      SupportStatus.closed => StatusColors.grey,
    };

class SupportListPage extends StatelessWidget {
  const SupportListPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<SupportListViewModel>();
    return Scaffold(
      appBar: AppBar(title: const Text('Support')),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () async {
          await context.push('/more/support/new');
          if (context.mounted) vm.load(refresh: true);
        },
        icon: const Icon(Icons.add),
        label: const Text('New request'),
      ),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: vm.items.isNotEmpty,
        onRetry: vm.load,
        builder: (_) => vm.items.isEmpty
            ? const EmptyView(icon: Icons.support_agent, message: 'You have not sent any support requests.')
            : RefreshIndicator(
                onRefresh: () => vm.load(refresh: true),
                child: ListView(padding: const EdgeInsets.fromLTRB(16, 16, 16, 88), children: [
                  for (final r in vm.items)
                    Card(
                      margin: const EdgeInsets.only(bottom: 8),
                      child: ListTile(
                        onTap: () async {
                          await context.push('/more/support/${r.id}');
                          if (context.mounted) vm.load(refresh: true);
                        },
                        title: Text(r.subject),
                        subtitle: Text('${r.requestNumber} · ${r.category.label}\n${Fmt.dateTime(r.lastActivityAt)}'),
                        isThreeLine: true,
                        trailing: StatusChip(r.status.label, supportStatusColor(r.status)),
                      ),
                    ),
                ]),
              ),
      ),
    );
  }
}

class SupportDetailPage extends StatefulWidget {
  const SupportDetailPage({super.key});

  @override
  State<SupportDetailPage> createState() => _SupportDetailPageState();
}

class _SupportDetailPageState extends State<SupportDetailPage> {
  final _message = TextEditingController();

  @override
  void dispose() {
    _message.dispose();
    super.dispose();
  }

  Future<void> _send(SupportDetailViewModel vm) async {
    final text = _message.text.trim();
    if (text.isEmpty) return;
    if (await vm.reply(text)) {
      _message.clear();
    } else if (mounted && vm.sendError != null) {
      showMessage(context, vm.sendError!, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<SupportDetailViewModel>();
    final d = vm.detail;
    return Scaffold(
      appBar: AppBar(title: Text(d?.request.requestNumber ?? 'Request')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: d != null,
        onRetry: vm.load,
        builder: (_) => Column(children: [
          Expanded(
            child: ListView(padding: const EdgeInsets.all(16), children: [
              Row(children: [Expanded(child: Text(d!.request.subject, style: Theme.of(context).textTheme.titleMedium)), StatusChip(d.request.status.label, supportStatusColor(d.request.status))]),
              const SizedBox(height: 12),
              for (final m in d.messages)
                Align(
                  alignment: m.fromOffice ? Alignment.centerLeft : Alignment.centerRight,
                  child: Container(
                    margin: const EdgeInsets.only(bottom: 8),
                    padding: const EdgeInsets.all(12),
                    constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * 0.8),
                    decoration: BoxDecoration(color: m.fromOffice ? Theme.of(context).colorScheme.surfaceContainerHighest : Theme.of(context).colorScheme.primaryContainer, borderRadius: BorderRadius.circular(12)),
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(m.fromOffice ? 'Office' : 'You', style: Theme.of(context).textTheme.labelSmall),
                      Text(m.body),
                      const SizedBox(height: 4),
                      Text(Fmt.dateTime(m.createdAt), style: Theme.of(context).textTheme.bodySmall),
                    ]),
                  ),
                ),
            ]),
          ),
          if (vm.canReply)
            SafeArea(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(12, 4, 12, 8),
                child: Row(children: [
                  Expanded(child: TextField(key: const Key('support-reply'), controller: _message, minLines: 1, maxLines: 4, decoration: const InputDecoration(hintText: 'Write a reply', border: OutlineInputBorder()))),
                  const SizedBox(width: 8),
                  IconButton.filled(onPressed: vm.sending ? null : () => _send(vm), icon: vm.sending ? const SizedBox(height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2)) : const Icon(Icons.send)),
                ]),
              ),
            )
          else
            const Padding(padding: EdgeInsets.all(12), child: Text('This request is closed.')),
        ]),
      ),
    );
  }
}

class NewSupportRequestPage extends StatefulWidget {
  const NewSupportRequestPage({super.key, required this.agreements});
  final List<PortalProperty> agreements;

  @override
  State<NewSupportRequestPage> createState() => _NewSupportRequestPageState();
}

class _NewSupportRequestPageState extends State<NewSupportRequestPage> {
  final _form = GlobalKey<FormState>();
  final _subject = TextEditingController();
  final _message = TextEditingController();
  SupportCategory _category = SupportCategory.general;
  int? _agreementId;

  @override
  void dispose() {
    _subject.dispose();
    _message.dispose();
    super.dispose();
  }

  Future<void> _submit(NewSupportRequestViewModel vm) async {
    if (!_form.currentState!.validate()) return;
    final result = await vm.create(_category, _subject.text.trim(), _message.text.trim(), _agreementId);
    if (result != null && mounted) {
      showMessage(context, 'Request ${result.request.requestNumber} sent.');
      context.pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<NewSupportRequestViewModel>();
    final agreements = {for (final a in widget.agreements) a.agreementId: a.agreementNumber};
    return Scaffold(
      appBar: AppBar(title: const Text('New support request')),
      body: SingleChildScrollView(
        padding: const EdgeInsets.all(16),
        child: Form(
          key: _form,
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            if (vm.saveError != null) Padding(padding: const EdgeInsets.only(bottom: 12), child: Text(vm.saveError!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
            DropdownButtonFormField<SupportCategory>(
              initialValue: _category,
              decoration: const InputDecoration(labelText: 'Topic', border: OutlineInputBorder()),
              items: [for (final c in SupportCategory.values) DropdownMenuItem(value: c, child: Text(c.label))],
              onChanged: (v) => setState(() => _category = v ?? _category),
            ),
            const SizedBox(height: 16),
            if (agreements.isNotEmpty) ...[
              DropdownButtonFormField<int?>(
                initialValue: _agreementId,
                decoration: const InputDecoration(labelText: 'Agreement (optional)', border: OutlineInputBorder()),
                items: [const DropdownMenuItem<int?>(value: null, child: Text('Not about a specific agreement')), for (final e in agreements.entries) DropdownMenuItem<int?>(value: e.key, child: Text(e.value))],
                onChanged: (v) => setState(() => _agreementId = v),
              ),
              const SizedBox(height: 16),
            ],
            TextFormField(
              key: const Key('support-subject'),
              controller: _subject,
              decoration: const InputDecoration(labelText: 'Subject', border: OutlineInputBorder()),
              validator: (v) => (v == null || v.trim().length < 3) ? 'Enter a short subject' : null,
            ),
            const SizedBox(height: 16),
            TextFormField(
              key: const Key('support-message'),
              controller: _message,
              maxLines: 5,
              decoration: const InputDecoration(labelText: 'Message', border: OutlineInputBorder(), alignLabelWithHint: true),
              validator: (v) => (v == null || v.trim().length < 3) ? 'Write your message' : null,
            ),
            const SizedBox(height: 20),
            FilledButton(onPressed: vm.saving ? null : () => _submit(vm), child: vm.saving ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2)) : const Text('Send request')),
          ]),
        ),
      ),
    );
  }
}
