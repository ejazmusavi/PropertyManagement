import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../../core/widgets/common.dart';
import '../../auth/presentation/auth_view_model.dart';
import 'profile_view_model.dart';

class ProfilePage extends StatelessWidget {
  const ProfilePage({super.key});

  Future<void> _edit(BuildContext context, ProfileViewModel vm) async {
    final p = vm.profile!;
    final mobile = TextEditingController(text: p.mobile);
    final email = TextEditingController(text: p.email ?? '');
    final address = TextEditingController(text: p.address ?? '');
    final form = GlobalKey<FormState>();
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (ctx) => Padding(
        padding: EdgeInsets.fromLTRB(16, 16, 16, MediaQuery.of(ctx).viewInsets.bottom + 16),
        child: Form(
          key: form,
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Text('Contact details', style: Theme.of(ctx).textTheme.titleMedium),
            const SizedBox(height: 12),
            TextFormField(key: const Key('profile-mobile'), controller: mobile, keyboardType: TextInputType.phone, decoration: const InputDecoration(labelText: 'Mobile', border: OutlineInputBorder()), validator: (v) => RegExp(r'^[0-9+\-\s()]{7,30}$').hasMatch(v ?? '') ? null : 'Enter a valid mobile number'),
            const SizedBox(height: 12),
            TextFormField(controller: email, keyboardType: TextInputType.emailAddress, decoration: const InputDecoration(labelText: 'E-mail', border: OutlineInputBorder()), validator: (v) => (v == null || v.isEmpty || v.contains('@')) ? null : 'Enter a valid e-mail'),
            const SizedBox(height: 12),
            TextFormField(controller: address, maxLines: 2, decoration: const InputDecoration(labelText: 'Address', border: OutlineInputBorder())),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: () async {
                if (!form.currentState!.validate()) return;
                final ok = await vm.save(mobile.text.trim(), email.text.trim().isEmpty ? null : email.text.trim(), address.text.trim().isEmpty ? null : address.text.trim());
                if (ctx.mounted) {
                  if (ok) {
                    Navigator.pop(ctx, true);
                  } else {
                    showMessage(ctx, vm.saveError ?? 'Could not save.', error: true);
                  }
                }
              },
              child: const Text('Save'),
            ),
          ]),
        ),
      ),
    );
    if (saved == true && context.mounted) showMessage(context, 'Contact details saved.');
  }

  Future<void> _password(BuildContext context, ProfileViewModel vm) async {
    final current = TextEditingController();
    final next = TextEditingController();
    final form = GlobalKey<FormState>();
    final done = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Change password'),
        content: Form(
          key: form,
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            TextFormField(controller: current, obscureText: true, decoration: const InputDecoration(labelText: 'Current password'), validator: (v) => (v == null || v.isEmpty) ? 'Enter your current password' : null),
            TextFormField(controller: next, obscureText: true, decoration: const InputDecoration(labelText: 'New password', helperText: 'At least 12 characters'), validator: (v) => (v == null || v.length < 12) ? 'Use at least 12 characters' : null),
          ]),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')),
          FilledButton(
            onPressed: () async {
              if (!form.currentState!.validate()) return;
              final ok = await vm.changePassword(current.text, next.text);
              if (ctx.mounted) {
                if (ok) {
                  Navigator.pop(ctx, true);
                } else {
                  showMessage(ctx, vm.saveError ?? 'Could not change the password.', error: true);
                }
              }
            },
            child: const Text('Change'),
          ),
        ],
      ),
    );
    if (done == true && context.mounted) showMessage(context, 'Your password was changed.');
  }

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<ProfileViewModel>();
    final p = vm.profile;
    return Scaffold(
      appBar: AppBar(title: const Text('My profile')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: p != null,
        onRetry: vm.load,
        builder: (_) => ListView(padding: const EdgeInsets.all(16), children: [
          SectionCard(title: p!.fullName, child: Column(children: [
            InfoRow('Customer code', p.customerCode),
            InfoRow('CNIC / passport', p.identityNumberMasked),
            InfoRow('Login e-mail', p.loginEmail),
            InfoRow('Mobile', p.mobile),
            InfoRow('E-mail', p.email ?? '-'),
            InfoRow('Address', p.address ?? '-'),
            const SizedBox(height: 4),
            Text('To change your name or identity details, please contact the office.', style: Theme.of(context).textTheme.bodySmall),
          ])),
          FilledButton.tonalIcon(onPressed: () => _edit(context, vm), icon: const Icon(Icons.edit_outlined), label: const Text('Edit contact details')),
          const SizedBox(height: 8),
          FilledButton.tonalIcon(onPressed: () => _password(context, vm), icon: const Icon(Icons.lock_reset), label: const Text('Change password')),
          const SizedBox(height: 8),
          OutlinedButton.icon(
            key: const Key('logout'),
            onPressed: () async {
              final ok = await showDialog<bool>(
                context: context,
                builder: (ctx) => AlertDialog(
                  title: const Text('Sign out?'),
                  content: const Text('You will need your password to sign in again.'),
                  actions: [TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')), FilledButton(onPressed: () => Navigator.pop(ctx, true), child: const Text('Sign out'))],
                ),
              );
              if (ok == true && context.mounted) await context.read<AuthViewModel>().signOut();
            },
            icon: const Icon(Icons.logout),
            label: const Text('Sign out'),
          ),
        ]),
      ),
    );
  }
}
