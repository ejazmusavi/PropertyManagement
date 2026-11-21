import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import 'core/app_dependencies.dart';
import 'core/navigation/app_router.dart';
import 'core/util/file_service.dart';
import 'features/auth/data/auth_repository.dart';
import 'features/dashboard/data/dashboard_repository.dart';
import 'features/installments/data/installments_repository.dart';
import 'features/ledger/data/ledger_repository.dart';
import 'features/notifications/data/notifications_repository.dart';
import 'features/payments/data/payments_repository.dart';
import 'features/profile/data/profile_repository.dart';
import 'features/properties/data/properties_repository.dart';
import 'features/support/data/support_repository.dart';
import 'features/auth/presentation/auth_view_model.dart';

class CustomerApp extends StatefulWidget {
  const CustomerApp({super.key, required this.dependencies});
  final AppDependencies dependencies;

  @override
  State<CustomerApp> createState() => _CustomerAppState();
}

class _CustomerAppState extends State<CustomerApp> {
  late final AuthViewModel _auth;
  late final GoRouter _router;

  @override
  void initState() {
    super.initState();
    _auth = AuthViewModel(widget.dependencies.auth);
    widget.dependencies.api.onSessionExpired = _auth.sessionExpired;
    _router = buildRouter(_auth);
    _auth.start();
  }

  @override
  void dispose() {
    _router.dispose();
    _auth.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final d = widget.dependencies;
    return MultiProvider(
      providers: [
        Provider<AppDependencies>.value(value: d),
        Provider<FileService>.value(value: d.files),
        Provider<AuthRepository>.value(value: d.auth),
        Provider<DashboardRepository>.value(value: d.dashboard),
        Provider<PropertiesRepository>.value(value: d.properties),
        Provider<InstallmentsRepository>.value(value: d.installments),
        Provider<PaymentsRepository>.value(value: d.payments),
        Provider<LedgerRepository>.value(value: d.ledger),
        Provider<NotificationsRepository>.value(value: d.notifications),
        Provider<SupportRepository>.value(value: d.support),
        Provider<ProfileRepository>.value(value: d.profile),
        ChangeNotifierProvider<AuthViewModel>.value(value: _auth),
      ],
      child: MaterialApp.router(
        title: 'My Properties',
        debugShowCheckedModeBanner: false,
        theme: ThemeData(colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF0D5C63)), useMaterial3: true),
        darkTheme: ThemeData(colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF0D5C63), brightness: Brightness.dark), useMaterial3: true),
        routerConfig: _router,
      ),
    );
  }
}
