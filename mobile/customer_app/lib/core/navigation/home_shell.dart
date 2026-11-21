import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../features/notifications/presentation/notifications_view_models.dart';

/// Bottom navigation of the signed-in app.
class HomeShell extends StatelessWidget {
  const HomeShell({super.key, required this.shell});
  final StatefulNavigationShell shell;

  @override
  Widget build(BuildContext context) => Scaffold(
        body: shell,
        bottomNavigationBar: NavigationBar(
          selectedIndex: shell.currentIndex,
          onDestinationSelected: (i) => shell.goBranch(i, initialLocation: i == shell.currentIndex),
          destinations: const [
            NavigationDestination(icon: Icon(Icons.dashboard_outlined), selectedIcon: Icon(Icons.dashboard), label: 'Home'),
            NavigationDestination(icon: Icon(Icons.home_work_outlined), selectedIcon: Icon(Icons.home_work), label: 'Properties'),
            NavigationDestination(icon: Icon(Icons.payments_outlined), selectedIcon: Icon(Icons.payments), label: 'Payments'),
            NavigationDestination(icon: Icon(Icons.receipt_long_outlined), selectedIcon: Icon(Icons.receipt_long), label: 'Statement'),
            NavigationDestination(icon: Icon(Icons.more_horiz), selectedIcon: Icon(Icons.more_horiz), label: 'More'),
          ],
        ),
      );
}

class MorePage extends StatelessWidget {
  const MorePage({super.key});

  @override
  Widget build(BuildContext context) {
    final unread = context.watch<NotificationsViewModel>().unread;
    return Scaffold(
      appBar: AppBar(title: const Text('More')),
      body: ListView(children: [
        ListTile(
          leading: Badge(isLabelVisible: unread > 0, label: Text('$unread'), child: const Icon(Icons.notifications_outlined)),
          title: const Text('Notifications'),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => context.push('/more/notifications'),
        ),
        ListTile(leading: const Icon(Icons.campaign_outlined), title: const Text('Announcements'), trailing: const Icon(Icons.chevron_right), onTap: () => context.push('/more/announcements')),
        ListTile(leading: const Icon(Icons.support_agent), title: const Text('Support requests'), trailing: const Icon(Icons.chevron_right), onTap: () => context.push('/more/support')),
        ListTile(leading: const Icon(Icons.account_balance_outlined), title: const Text('How to pay'), trailing: const Icon(Icons.chevron_right), onTap: () => context.push('/payments/instructions')),
        ListTile(leading: const Icon(Icons.person_outline), title: const Text('My profile'), trailing: const Icon(Icons.chevron_right), onTap: () => context.push('/more/profile')),
      ]),
    );
  }
}
