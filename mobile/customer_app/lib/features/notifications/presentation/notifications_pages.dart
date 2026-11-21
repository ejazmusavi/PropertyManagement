import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../models/notification_models.dart';
import 'notifications_view_models.dart';

IconData _iconFor(int kind) => switch (kind) {
      2 => Icons.payments_outlined,
      3 => Icons.support_agent,
      4 => Icons.person_outline,
      _ => Icons.notifications_outlined,
    };

class NotificationsPage extends StatelessWidget {
  const NotificationsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<NotificationsViewModel>();
    return Scaffold(
      appBar: AppBar(title: const Text('Notifications'), actions: [
        if (vm.unread > 0) TextButton(onPressed: vm.markAllRead, child: const Text('Mark all read')),
      ]),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: vm.items.isNotEmpty,
        onRetry: vm.load,
        builder: (_) => vm.items.isEmpty
            ? const EmptyView(icon: Icons.notifications_none, message: 'No notifications yet.')
            : RefreshIndicator(
                onRefresh: () => vm.load(refresh: true),
                child: ListView(children: [
                  for (final n in vm.items)
                    ListTile(
                      leading: Icon(_iconFor(n.kind), color: n.isRead ? null : Theme.of(context).colorScheme.primary),
                      title: Text(n.title, style: TextStyle(fontWeight: n.isRead ? FontWeight.normal : FontWeight.w700)),
                      subtitle: Text('${n.message}\n${Fmt.dateTime(n.createdAt)}'),
                      isThreeLine: true,
                      onTap: () => vm.markRead(n),
                    ),
                  if (vm.hasMore) TextButton(onPressed: vm.loadMore, child: const Text('Load more')),
                ]),
              ),
      ),
    );
  }
}

class AnnouncementsPage extends StatelessWidget {
  const AnnouncementsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<AnnouncementsViewModel>();
    return Scaffold(
      appBar: AppBar(title: const Text('Announcements')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        onRetry: vm.load,
        builder: (_) => vm.items.isEmpty
            ? const EmptyView(icon: Icons.campaign_outlined, message: 'No announcements right now.')
            : RefreshIndicator(
                onRefresh: vm.load,
                child: ListView(padding: const EdgeInsets.all(16), children: [
                  for (final Announcement a in vm.items)
                    SectionCard(title: a.title, child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(a.body),
                      const SizedBox(height: 8),
                      Text(Fmt.date(a.publishedAt), style: Theme.of(context).textTheme.bodySmall),
                    ])),
                ]),
              ),
      ),
    );
  }
}
