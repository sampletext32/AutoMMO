using AutoMmo.Worker.Config;
using Prometheus;

namespace AutoMmo.Worker;

public class MmoMetrics
{
    private readonly Counter _authRequestsCounter;
    private readonly Counter _stepsCounter;
    private readonly Counter _attacksCounter;
    private readonly Counter _attackClicksCounter;
    private readonly Counter _resourcesCounter;
    private readonly Counter _resourceClicksCounter;
    private readonly Counter _captchaCounter;
    private readonly Gauge _isEnabledGauge;
    private readonly Counter _foundItemsCounter;
    private readonly Counter _resourcesStatsCounter;

    public MmoMetrics(IMetricFactory metricFactory, WorkerConfig config)
    {
        _authRequestsCounter = metricFactory.CreateCounter("mmo_auth_requests_total", "Total number of MMO authentication requests");
        _stepsCounter = metricFactory.CreateCounter("mmo_steps", "Total number of steps", ["type"]);
        _foundItemsCounter = metricFactory.CreateCounter("mmo_found_items", "Total number of found items", ["rarity", "name"]);
        _attacksCounter = metricFactory.CreateCounter("mmo_attacks", "Total number of attacks", ["mob_name"]);
        _attackClicksCounter = metricFactory.CreateCounter("mmo_attack_clicks", "Total number of clicks in all attacks");
        _resourcesCounter = metricFactory.CreateCounter("mmo_resources", "Total number of resources", ["type"]);
        _resourcesStatsCounter = metricFactory.CreateCounter("mmo_resources_stats", "Total number of resources", ["rarity", "name"]);
        _resourceClicksCounter = metricFactory.CreateCounter("mmo_resource_clicks", "Total number of clicks in all resources", ["item_type", "item_id"]);
        _captchaCounter = metricFactory.CreateCounter("mmo_captcha", "Total number of captchas encountered");
        _isEnabledGauge = metricFactory.CreateGauge("mmo_is_enabled", "Indicates if the MMO worker is enabled or not");
        
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => _isEnabledGauge.Set(config.IsEnabled ? 1 : 0));
    }
    
    public void IncrementAuthRequests() => _authRequestsCounter.Inc();
    public void IncrementSteps(string type) => _stepsCounter.WithLabels([type]).Inc();
    public void IncrementItems(string rarity, string name) => _foundItemsCounter.WithLabels([rarity, name]).Inc();
    public void IncrementAttacks(string mobName) => _attacksCounter.WithLabels([mobName]).Inc();
    public void IncrementAttackClicks() => _attackClicksCounter.Inc();
    public void IncrementResources(string type) => _resourcesCounter.WithLabels([type]).Inc();
    public void IncrementResourcesStats(string rarity, string itemId) => _resourcesStatsCounter.WithLabels([rarity, itemId]).Inc();
    public void IncrementResourceClicks(string itemType, string itemId) => _resourceClicksCounter.WithLabels([itemType, itemId]).Inc();
    public void IncrementCaptcha() => _captchaCounter.Inc();
}