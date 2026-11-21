import 'dart:typed_data';

import 'package:customer_app/app.dart';
import 'package:customer_app/core/app_dependencies.dart';
import 'package:customer_app/core/network/api_client.dart';
import 'package:customer_app/core/storage/token_storage.dart';
import 'package:customer_app/features/payments/presentation/payments_view_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_server.dart';

class _Harness {
  _Harness() {
    api = ApiClient(storage: storage, client: server.client, baseUrl: 'http://test');
    deps = AppDependencies(storage: storage, files: files, client: api);
  }
  final server = FakeServer();
  final storage = MemoryTokenStorage();
  final files = FakeFileService();
  late final ApiClient api;
  late final AppDependencies deps;
}

Future<void> _boot(WidgetTester tester, _Harness h) async {
  await tester.binding.setSurfaceSize(const Size(500, 1400));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(CustomerApp(dependencies: h.deps));
  await tester.pumpAndSettle();
}

Future<void> _login(WidgetTester tester, {String email = 'customer@example.com', String password = 'Str0ng#Passw0rd!'}) async {
  await tester.enterText(find.byKey(const Key('login-email')), email);
  await tester.enterText(find.byKey(const Key('login-password')), password);
  await tester.tap(find.byKey(const Key('login-submit')));
  await tester.pumpAndSettle();
}

Future<void> _tab(WidgetTester tester, String label) async {
  await tester.tap(find.descendant(of: find.byType(NavigationBar), matching: find.text(label)));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('login validates input, rejects wrong passwords and non-customer accounts', (tester) async {
    final h = _Harness();
    await _boot(tester, h);
    expect(find.text('Sign in'), findsWidgets);

    await tester.tap(find.byKey(const Key('login-submit')));
    await tester.pumpAndSettle();
    expect(find.text('Enter your e-mail address'), findsOneWidget);
    expect(find.text('Enter your password'), findsOneWidget);

    await _login(tester, password: 'wrong');
    expect(find.text('Invalid email or password.'), findsOneWidget);
    expect(h.storage.value, isNull);

    await _login(tester, email: 'staff@example.com');
    expect(find.textContaining('cannot use the customer app'), findsOneWidget);
    expect(h.storage.value, isNull); // the tokens of a non-customer account are not kept
    expect(find.textContaining('Welcome'), findsNothing);
  });

  testWidgets('a customer signs in and sees dashboard figures, properties, agreement and installments', (tester) async {
    final h = _Harness();
    await _boot(tester, h);
    await _login(tester);

    // dashboard (figures exactly as the backend sent them)
    expect(find.text('Welcome, Ali Khan'), findsOneWidget);
    expect(find.text('Rs 5,000,000.00'), findsOneWidget);
    expect(find.text('Rs 600,000.00'), findsOneWidget);
    expect(find.text('Rs 162,500.00'), findsWidgets);
    expect(find.text('Upcoming dues'), findsOneWidget);
    expect(find.textContaining('is overdue'), findsOneWidget);
    expect(h.storage.value, isNotNull);

    // my properties and the agreement
    await _tab(tester, 'Properties');
    expect(find.text('Residential Plot 101'), findsOneWidget);
    expect(find.text('Green Valley (GV)'), findsOneWidget);
    expect(find.text('5 Marla'), findsOneWidget);
    await tester.tap(find.text('Residential Plot 101'));
    await tester.pumpAndSettle();
    expect(find.text('AGR-000007'), findsWidgets);
    expect(find.text('Total payable'), findsOneWidget);
    expect(find.text('Rs 5,000,000.00'), findsWidgets);

    // installments with filters
    await tester.tap(find.widgetWithText(FilledButton, 'Installments'));
    await tester.pumpAndSettle();
    expect(find.text('Payment schedule'), findsOneWidget);
    expect(find.text('Down payment'), findsOneWidget);
    expect(find.text('Overdue'), findsWidgets);
    await tester.tap(find.widgetWithText(ChoiceChip, 'Overdue'));
    await tester.pumpAndSettle();
    expect(find.text('Down payment'), findsNothing);
    expect(find.text('Monthly installment'), findsOneWidget);
    await tester.tap(find.widgetWithText(ChoiceChip, 'Paid'));
    await tester.pumpAndSettle();
    expect(find.text('Down payment'), findsOneWidget);
  });

  testWidgets('payments: history, receipt download, instructions and statement', (tester) async {
    final h = _Harness();
    await _boot(tester, h);
    await _login(tester);

    await _tab(tester, 'Payments');
    expect(find.text('PAY-000011 · 01 Sep 2026\nBank transfer · AGR-000007', findRichText: true), findsOneWidget);
    expect(find.text('Received'), findsOneWidget);
    await tester.tap(find.textContaining('PAY-000011'));
    await tester.pumpAndSettle();
    expect(find.text('Applied to'), findsOneWidget);
    expect(find.text('Your payment proof'), findsOneWidget);

    await tester.tap(find.byKey(const Key('download-receipt')));
    await tester.pumpAndSettle();
    expect(h.files.opened, hasLength(1));
    expect(h.files.opened.single.fileName, 'RCP-000021.pdf');
    expect(h.files.opened.single.contentType, 'application/pdf');
    expect(String.fromCharCodes(h.files.opened.single.bytes), startsWith('%PDF'));

    await tester.pageBack();
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Payment instructions'));
    await tester.pumpAndSettle();
    expect(find.text('HBL'), findsOneWidget);
    expect(find.text('0012-3456789'), findsOneWidget);
    expect(find.textContaining('Pay to the company account'), findsOneWidget);
    await tester.pageBack();
    await tester.pumpAndSettle();

    await _tab(tester, 'Statement');
    expect(find.text('Outstanding balance'), findsOneWidget);
    expect(find.text('Rs 162,500.00'), findsWidgets);
    expect(find.text('Payment PAY-000011 received'), findsOneWidget);
    expect(find.text('Transactions'), findsOneWidget);
  });

  testWidgets('payment proof form validates before anything is sent', (tester) async {
    final h = _Harness();
    await _boot(tester, h);
    await _login(tester);
    await _tab(tester, 'Payments');
    await tester.tap(find.text('Submit payment proof'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('proof-submit')));
    await tester.pumpAndSettle();
    expect(find.text('Choose the payment method'), findsOneWidget);
    expect(find.text('Enter an amount greater than zero (at most two decimals)'), findsOneWidget);
    expect(find.text('Attach the bank slip or a screenshot of the payment.'), findsOneWidget);
    expect(h.server.proofPosts, 0);
  });

  testWidgets('notifications, announcements, support and sign out', (tester) async {
    final h = _Harness();
    await _boot(tester, h);
    await _login(tester);

    await _tab(tester, 'More');
    await tester.tap(find.text('Notifications'));
    await tester.pumpAndSettle();
    expect(find.text('Payment received'), findsOneWidget);
    await tester.tap(find.text('Payment received'));
    await tester.pumpAndSettle();
    expect(h.server.requests.any((r) => r.method == 'POST' && r.path == '/portal/notifications/5/read'), isTrue);
    await tester.pageBack();
    await tester.pumpAndSettle();

    await tester.tap(find.text('Announcements'));
    await tester.pumpAndSettle();
    expect(find.text('Office closed Friday'), findsOneWidget);
    await tester.pageBack();
    await tester.pumpAndSettle();

    await tester.tap(find.text('Support requests'));
    await tester.pumpAndSettle();
    expect(find.text('Receipt missing'), findsOneWidget);
    await tester.tap(find.text('Receipt missing'));
    await tester.pumpAndSettle();
    expect(find.text('I did not get my receipt.'), findsOneWidget);
    expect(find.text('We will e-mail it today.'), findsOneWidget);
    await tester.pageBack();
    await tester.pumpAndSettle();
    await tester.pageBack();
    await tester.pumpAndSettle();

    await tester.tap(find.text('My profile'));
    await tester.pumpAndSettle();
    expect(find.text('Ali Khan'), findsOneWidget);
    expect(find.text('*********4567'), findsOneWidget);
    await tester.tap(find.byKey(const Key('logout')));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Sign out'));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('login-submit')), findsOneWidget);
    expect(h.storage.value, isNull);
    expect(h.server.requests.any((r) => r.path == '/auth/logout'), isTrue);
  });

  testWidgets('forgot password: request a code, then set a new password', (tester) async {
    final h = _Harness();
    await _boot(tester, h);
    await tester.tap(find.text('Forgot password?'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byKey(const Key('reset-email')), 'customer@example.com');
    await tester.tap(find.byKey(const Key('reset-submit')));
    await tester.pumpAndSettle();
    expect(h.server.requests.any((r) => r.path == '/auth/forgot-password'), isTrue);
    expect(find.byKey(const Key('reset-code')), findsOneWidget);

    await tester.enterText(find.byKey(const Key('reset-code')), 'BADCODE');
    await tester.enterText(find.byKey(const Key('reset-password')), 'Str0ng#Passw0rd!2');
    await tester.tap(find.byKey(const Key('reset-submit')));
    await tester.pumpAndSettle();
    expect(find.text('The reset code is invalid or has expired.'), findsOneWidget);

    await tester.enterText(find.byKey(const Key('reset-code')), 'GOODCODE');
    await tester.tap(find.byKey(const Key('reset-submit')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('login-submit')), findsOneWidget); // back at the sign-in screen
  });

  test('proof submission sends the form without a customer id and reuses its idempotency key', () async {
    final h = _Harness();
    await h.api.setSession(StoredSession(accessToken: 'access-1', accessExpiresAt: DateTime.utc(2099), refreshToken: 'refresh-1', refreshExpiresAt: DateTime.utc(2099)));
    final vm = SubmitProofViewModel(h.deps.payments);
    final file = UploadFile('slip.png', Uint8List.fromList([1, 2, 3]), 'image/png');
    Future<bool> send() => vm.submit(agreementId: 7, methodId: 2, amount: 250000, date: DateTime(2026, 11, 20), reference: 'TRX-9', bankName: 'HBL', file: file);
    expect(await send(), isTrue);
    expect(await send(), isTrue); // a retry after a lost response
    expect(h.server.proofPosts, 2);
    final posts = h.server.requests.where((r) => r.method == 'POST' && r.path == '/portal/payments').toList();
    for (final p in posts) {
      expect(p.body, contains('name="AgreementId"'));
      expect(p.body, contains('250000.00'));
      expect(p.body, contains('2026-11-20'));
      expect(p.body, contains('filename="slip.png"'));
      expect(p.body.toLowerCase(), isNot(contains('customerid')));
    }
    String key(String body) => RegExp(r'name="IdempotencyKey"\r\n\r\n([0-9a-f]+)').firstMatch(body)!.group(1)!;
    expect(key(posts[0].body), key(posts[1].body));
    expect(vm.result!.payment.status.label, 'Pending verification');
  });
}
