import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../../core/util/format.dart';
import '../../../core/widgets/common.dart';
import '../models/property_models.dart';
import 'properties_view_models.dart';

class PropertiesPage extends StatelessWidget {
  const PropertiesPage({super.key});

  @override
  Widget build(BuildContext context) {
    final vm = context.watch<PropertiesViewModel>();
    return Scaffold(
      appBar: AppBar(title: const Text('My properties')),
      body: AsyncBody(
        status: vm.status,
        error: vm.error,
        hasData: vm.items.isNotEmpty,
        onRetry: vm.load,
        builder: (_) => vm.items.isEmpty
            ? const EmptyView(icon: Icons.home_work_outlined, message: 'You do not have any properties yet.')
            : RefreshIndicator(
                onRefresh: () => vm.load(refresh: true),
                child: ListView.builder(
                  padding: const EdgeInsets.all(16),
                  itemCount: vm.items.length,
                  itemBuilder: (context, i) => _PropertyCard(vm.items[i]),
                ),
              ),
      ),
    );
  }
}

class _PropertyCard extends StatelessWidget {
  const _PropertyCard(this.p);
  final PortalProperty p;

  @override
  Widget build(BuildContext context) {
    final cancelled = p.agreementStatus == AgreementStatus.cancelled || p.bookingStatus == BookingStatus.cancelled;
    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: InkWell(
        onTap: () => context.push('/agreement/${p.agreementId}'),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Expanded(child: Text('${p.propertyType} ${p.propertyNumber}', style: Theme.of(context).textTheme.titleMedium)),
              StatusChip(cancelled ? 'Cancelled' : p.bookingStatus.label, cancelled ? StatusColors.red : StatusColors.green),
            ]),
            const SizedBox(height: 4),
            Text('${p.projectName} (${p.projectCode})', style: Theme.of(context).textTheme.bodyMedium),
            const Divider(height: 20),
            InfoRow('Property code', p.propertyCode),
            InfoRow('Category', p.category),
            InfoRow('Size', Fmt.size(p.size, p.unit)),
            if (p.sharePercent < 100) InfoRow('Your share', '${p.sharePercent.toStringAsFixed(2)} %'),
            InfoRow('Agreement', p.agreementNumber),
            InfoRow('Payment plan', p.paymentMode.label),
          ]),
        ),
      ),
    );
  }
}
