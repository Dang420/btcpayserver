using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayoutData = BTCPayServer.Data.PayoutData;
using PayoutProcessorData = BTCPayServer.Data.PayoutProcessorData;

namespace BTCPayServer.PayoutProcessors;

public abstract class BaseAutomatedPayoutProcessor<T> : BaseAsyncService where T : AutomatedPayoutBlob
{
    protected readonly StoreRepository _storeRepository;
    protected readonly PayoutProcessorData _PayoutProcesserSettings;
    protected readonly ApplicationDbContextFactory _applicationDbContextFactory;
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    protected readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    protected readonly PaymentMethodId PaymentMethodId;
    private readonly IPluginHookService _pluginHookService;

    protected BaseAutomatedPayoutProcessor(
        ILoggerFactory logger,
        StoreRepository storeRepository,
        PayoutProcessorData payoutProcesserSettings,
        ApplicationDbContextFactory applicationDbContextFactory,
        PullPaymentHostedService pullPaymentHostedService,
        BTCPayNetworkProvider btcPayNetworkProvider,
        IPluginHookService pluginHookService) : base(logger.CreateLogger($"{payoutProcesserSettings.Processor}:{payoutProcesserSettings.StoreId}:{payoutProcesserSettings.PaymentMethod}"))
    {
        _storeRepository = storeRepository;
        _PayoutProcesserSettings = payoutProcesserSettings;
        PaymentMethodId = _PayoutProcesserSettings.GetPaymentMethodId();
        _applicationDbContextFactory = applicationDbContextFactory;
        _pullPaymentHostedService = pullPaymentHostedService;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _pluginHookService = pluginHookService;
        this.NoLogsOnExit = true;
    }

    internal override Task[] InitializeTasks()
    {
        return new[] { CreateLoopTask(Act) };
    }

    protected abstract Task Process(ISupportedPaymentMethod paymentMethod, List<PayoutData> payouts);

    private async Task Act()
    {
        var store = await _storeRepository.FindStore(_PayoutProcesserSettings.StoreId);
        var paymentMethod = store?.GetEnabledPaymentMethods(_btcPayNetworkProvider)?.FirstOrDefault(
            method =>
                method.PaymentId == PaymentMethodId);

        var blob = GetBlob(_PayoutProcesserSettings);
        if (paymentMethod is not null)
        {
            
            // Allow plugins to do something before the automatic payouts are executed
            await _pluginHookService.ApplyFilter("before-automated-payout-processing", 
                new BeforePayoutFilterData(store, paymentMethod));

            await using var context = _applicationDbContextFactory.CreateContext();
            var payouts = await PullPaymentHostedService.GetPayouts(
                new PullPaymentHostedService.PayoutQuery()
                {
                    States = new[] { PayoutState.AwaitingPayment },
                    PaymentMethods = new[] { _PayoutProcesserSettings.PaymentMethod },
                    Stores = new[] { _PayoutProcesserSettings.StoreId }
                }, context);
            if (payouts.Any())
            {
                Logs.PayServer.LogInformation($"{payouts.Count} found to process. Starting (and after will sleep for {blob.Interval})");
                await Process(paymentMethod, payouts);
                await context.SaveChangesAsync();
                
                // Allow plugins do to something after automatic payout processing
                await _pluginHookService.ApplyFilter("after-automated-payout-processing", 
                    new AfterPayoutFilterData(store, paymentMethod, payouts));
            }
        }
        await Task.Delay(blob.Interval, CancellationToken);
    }

    public static T GetBlob(PayoutProcessorData payoutProcesserSettings)
    {
        return payoutProcesserSettings.HasTypedBlob<T>().GetBlob();
    }
}
