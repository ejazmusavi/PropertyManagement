import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../features/auth/presentation/auth_view_model.dart';
import '../../features/auth/presentation/forgot_password_page.dart';
import '../../features/auth/presentation/login_page.dart';
import '../../features/dashboard/presentation/dashboard_page.dart';
import '../../features/dashboard/presentation/dashboard_view_model.dart';
import '../../features/installments/data/installments_repository.dart';
import '../../features/installments/presentation/installments_page.dart';
import '../../features/installments/presentation/installments_view_model.dart';
import '../../features/ledger/data/ledger_repository.dart';
import '../../features/ledger/presentation/ledger_pages.dart';
import '../../features/ledger/presentation/ledger_view_models.dart';
import '../../features/notifications/data/notifications_repository.dart';
import '../../features/notifications/presentation/notifications_pages.dart';
import '../../features/notifications/presentation/notifications_view_models.dart';
import '../../features/payments/data/payments_repository.dart';
import '../../features/payments/presentation/instructions_page.dart';
import '../../features/payments/presentation/online_payment_page.dart';
import '../../features/payments/presentation/payment_detail_page.dart';
import '../../features/payments/presentation/payments_page.dart';
import '../../features/payments/presentation/payments_view_models.dart';
import '../../features/payments/presentation/submit_proof_page.dart';
import '../../features/profile/data/profile_repository.dart';
import '../../features/profile/presentation/profile_page.dart';
import '../../features/profile/presentation/profile_view_model.dart';
import '../../features/properties/data/properties_repository.dart';
import '../../features/properties/presentation/agreement_page.dart';
import '../../features/properties/presentation/properties_page.dart';
import '../../features/properties/presentation/properties_view_models.dart';
import '../../features/support/data/support_repository.dart';
import '../../features/support/presentation/support_pages.dart';
import '../../features/support/presentation/support_view_models.dart';
import '../app_dependencies.dart';
import '../util/file_service.dart';
import 'home_shell.dart';

/// Screens that need the customer's agreements (payment proof, online payment, support) read them from the properties list.
List<T> _agreementsOf<T>(BuildContext context, List<T> Function(PropertiesViewModel vm) pick) => pick(context.read<PropertiesViewModel>());

GoRouter buildRouter(AuthViewModel auth) {
  return GoRouter(
    initialLocation: '/splash',
    refreshListenable: auth,
    redirect: (context, state) {
      final loc = state.matchedLocation;
      final open = loc == '/login' || loc == '/forgot';
      switch (auth.state) {
        case AuthState.unknown:
          return loc == '/splash' ? null : '/splash';
        case AuthState.signedOut:
          return open ? null : '/login';
        case AuthState.signedIn:
          return (open || loc == '/splash') ? '/home' : null;
      }
    },
    routes: [
      GoRoute(path: '/splash', builder: (_, _) => const Scaffold(body: Center(child: CircularProgressIndicator()))),
      GoRoute(path: '/login', builder: (_, _) => const LoginPage()),
      GoRoute(path: '/forgot', builder: (_, _) => const ForgotPasswordPage()),
      ShellRoute(
        builder: (context, state, child) => SignedInScope(child: child),
        routes: [
          StatefulShellRoute.indexedStack(
            builder: (context, state, shell) => HomeShell(shell: shell),
            branches: [
              StatefulShellBranch(routes: [GoRoute(path: '/home', builder: (_, _) => const DashboardPage())]),
              StatefulShellBranch(routes: [GoRoute(path: '/properties', builder: (_, _) => const PropertiesPage())]),
              StatefulShellBranch(routes: [
                GoRoute(path: '/payments', builder: (context, _) => PaymentsPage(onlineEnabled: context.watch<DashboardViewModel>().data?.onlinePaymentsEnabled ?? false)),
              ]),
              StatefulShellBranch(routes: [GoRoute(path: '/statement', builder: (_, _) => const LedgerPage())]),
              StatefulShellBranch(routes: [GoRoute(path: '/more', builder: (_, _) => const MorePage())]),
            ],
          ),
          GoRoute(
            path: '/agreement/:id',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => AgreementViewModel(c.read<PropertiesRepository>(), int.parse(state.pathParameters['id']!))..load(),
              child: const AgreementPage(),
            ),
          ),
          GoRoute(
            path: '/agreement/:id/installments',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => InstallmentsViewModel(c.read<InstallmentsRepository>(), int.parse(state.pathParameters['id']!))..load(),
              child: const InstallmentsPage(),
            ),
          ),
          GoRoute(
            path: '/agreement/:id/statement',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => AgreementLedgerViewModel(c.read<LedgerRepository>(), int.parse(state.pathParameters['id']!))..load(),
              child: const AgreementLedgerPage(),
            ),
          ),
          GoRoute(
            path: '/payments/submit',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => SubmitProofViewModel(c.read<PaymentsRepository>())..load(),
              child: SubmitProofPage(agreements: _agreementsOf(context, (vm) => vm.items)),
            ),
          ),
          GoRoute(
            path: '/payments/instructions',
            builder: (context, state) => ChangeNotifierProvider(create: (c) => InstructionsViewModel(c.read<PaymentsRepository>())..load(), child: const InstructionsPage()),
          ),
          GoRoute(
            path: '/payments/online',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => OnlinePaymentViewModel(c.read<PaymentsRepository>()),
              child: OnlinePaymentPage(agreements: _agreementsOf(context, (vm) => vm.items)),
            ),
          ),
          GoRoute(
            path: '/payments/:id',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => PaymentDetailViewModel(c.read<PaymentsRepository>(), c.read<FileService>(), int.parse(state.pathParameters['id']!))..load(),
              child: const PaymentDetailPage(),
            ),
          ),
          GoRoute(path: '/more/notifications', builder: (_, _) => const NotificationsPage()),
          GoRoute(
            path: '/more/announcements',
            builder: (context, state) => ChangeNotifierProvider(create: (c) => AnnouncementsViewModel(c.read<NotificationsRepository>())..load(), child: const AnnouncementsPage()),
          ),
          GoRoute(
            path: '/more/support',
            builder: (context, state) => ChangeNotifierProvider(create: (c) => SupportListViewModel(c.read<SupportRepository>())..load(), child: const SupportListPage()),
          ),
          GoRoute(
            path: '/more/support/new',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => NewSupportRequestViewModel(c.read<SupportRepository>()),
              child: NewSupportRequestPage(agreements: _agreementsOf(context, (vm) => vm.items)),
            ),
          ),
          GoRoute(
            path: '/more/support/:id',
            builder: (context, state) => ChangeNotifierProvider(
              create: (c) => SupportDetailViewModel(c.read<SupportRepository>(), int.parse(state.pathParameters['id']!))..load(),
              child: const SupportDetailPage(),
            ),
          ),
          GoRoute(
            path: '/more/profile',
            builder: (context, state) => ChangeNotifierProvider(create: (c) => ProfileViewModel(c.read<ProfileRepository>())..load(), child: const ProfilePage()),
          ),
        ],
      ),
    ],
  );
}

/// State holders that live as long as the customer is signed in (disposed at sign out so no data survives for the next user).
class SignedInScope extends StatelessWidget {
  const SignedInScope({super.key, required this.child});
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final deps = context.read<AppDependencies>();
    return MultiProvider(
      providers: [
        ChangeNotifierProvider(create: (_) => DashboardViewModel(deps.dashboard)..load()),
        ChangeNotifierProvider(create: (_) => PropertiesViewModel(deps.properties)..load()),
        ChangeNotifierProvider(create: (_) => PaymentsViewModel(deps.payments)..load()),
        ChangeNotifierProvider(create: (_) => LedgerViewModel(deps.ledger)..load()),
        ChangeNotifierProvider(create: (_) => NotificationsViewModel(deps.notifications)..load()),
      ],
      child: child,
    );
  }
}
