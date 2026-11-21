import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/widgets/common.dart';
import 'auth_view_model.dart';

/// Step 1: ask for a reset code. Step 2: enter the code and a new password.
class ForgotPasswordPage extends StatefulWidget {
  const ForgotPasswordPage({super.key});

  @override
  State<ForgotPasswordPage> createState() => _ForgotPasswordPageState();
}

class _ForgotPasswordPageState extends State<ForgotPasswordPage> {
  final _email = TextEditingController();
  final _code = TextEditingController();
  final _password = TextEditingController();
  final _form = GlobalKey<FormState>();
  bool _codeSent = false;

  @override
  void dispose() {
    _email.dispose();
    _code.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _request() async {
    if (!_form.currentState!.validate()) return;
    final ok = await context.read<AuthViewModel>().requestReset(_email.text);
    if (ok && mounted) {
      setState(() => _codeSent = true);
      showMessage(context, 'If this e-mail has an account, a reset code was sent.');
    }
  }

  Future<void> _reset() async {
    if (!_form.currentState!.validate()) return;
    final auth = context.read<AuthViewModel>();
    final ok = await auth.resetPassword(_email.text, _code.text, _password.text);
    if (ok && mounted) {
      showMessage(context, 'Your password was changed. Please sign in.');
      context.go('/login');
    }
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthViewModel>();
    return Scaffold(
      appBar: AppBar(title: const Text('Forgot password'), leading: BackButton(onPressed: () => context.go('/login'))),
      body: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Form(
            key: _form,
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              Text(_codeSent ? 'Enter the code you received and choose a new password.' : 'Enter the e-mail of your account. We will send you a reset code.'),
              const SizedBox(height: 16),
              if (auth.error != null)
                Padding(padding: const EdgeInsets.only(bottom: 12), child: Text(auth.error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
              TextFormField(
                key: const Key('reset-email'),
                controller: _email,
                keyboardType: TextInputType.emailAddress,
                decoration: const InputDecoration(labelText: 'E-mail', border: OutlineInputBorder()),
                validator: (v) => (v == null || !v.contains('@')) ? 'Enter your e-mail address' : null,
              ),
              if (_codeSent) ...[
                const SizedBox(height: 16),
                TextFormField(
                  key: const Key('reset-code'),
                  controller: _code,
                  decoration: const InputDecoration(labelText: 'Reset code', border: OutlineInputBorder()),
                  validator: (v) => (v == null || v.trim().isEmpty) ? 'Enter the reset code' : null,
                ),
                const SizedBox(height: 16),
                TextFormField(
                  key: const Key('reset-password'),
                  controller: _password,
                  obscureText: true,
                  decoration: const InputDecoration(labelText: 'New password', helperText: 'At least 12 characters with upper and lower case, a digit and a symbol', border: OutlineInputBorder()),
                  validator: (v) => (v == null || v.length < 12) ? 'Use at least 12 characters' : null,
                ),
              ],
              const SizedBox(height: 20),
              FilledButton(
                key: const Key('reset-submit'),
                onPressed: auth.busy ? null : (_codeSent ? _reset : _request),
                child: auth.busy ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2)) : Text(_codeSent ? 'Change password' : 'Send reset code'),
              ),
              if (_codeSent) TextButton(onPressed: auth.busy ? null : _request, child: const Text('Send the code again')),
            ]),
          ),
        ),
      ),
    );
  }
}
