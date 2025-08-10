using System.Media;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMmo.Worker.Config;
using HtmlAgilityPack;
using Microsoft.Playwright;

namespace AutoMmo.Worker;

public record struct ItemWithRarity(string Rarity, string Name);

public class BackgroundWorker : BackgroundService
{
    private readonly WorkerConfig _config;
    private readonly MmoMetrics _mmoMetrics;
    private readonly ILogger<BackgroundWorker> _logger;

    public BackgroundWorker(WorkerConfig config, MmoMetrics mmoMetrics, ILogger<BackgroundWorker> logger)
    {
        _config = config;
        _mmoMetrics = mmoMetrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background worker started.");

        using var playwright = await Playwright.CreateAsync();
        // Launch browser (headless or with UI)
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            HandleSIGHUP = false,
            HandleSIGINT = false,
            HandleSIGTERM = false,
            Headless = false // Set to true if you want headless mode
        });

        // Create new browser page
        var page = await browser.NewPageAsync();

        await Auth(page);

        // Navigate to the target page
        await page.GotoAsync("https://web.simple-mmo.com/travel");

        await MainLoop(page, stoppingToken);

        _logger.LogInformation("Background worker finished.");
    }

    // record StepResponseBody(
    //     exp_amount: 27416
    //     exp_percentage    : 20    
    //     gold_amount    : 0    
    //     guild_raid_exp    : false    
    //     heading    : "You take a step..."    
    //     level    : 9063
    //     );

    record AttackResponseBody(
        // string type,
        // string title,
        // string result,
        // int player_hp,
        // int player_hp_percentage,
        // int opponent_hp,
        // int opponent_hp_percentage,
        // int damage_given_to_opponent,
        // int damage_given_to_player,
        // string confirm_button_text,
        // bool show_confirm_button,
        [property: JsonPropertyName("rewards")]
        List<string>? Rewards
    );

    public static async Task<List<ItemWithRarity>> GetAttackRewards(IRequest request)
    {
        // {
        //     "type": "success",
        //     "title": "Winner winner chicken dinner!",
        //     "result": "You have won:<br><div> <img src='\/img\/icons\/S_Light01.png' class='h-4'> 70,896 EXP<\/div><div><img src='\/img\/icons\/I_GoldCoin.png'  class='h-4'> 2,270 gold<\/div><div><img src='\/img\/icons\/two\/32px\/Gem_32.png'  class='h-4'> 1x <span class='common-item' onclick='retrieveItem(11602);Swal.close();this.close();'>Jewel (Legacy)<\/span><\/div>",
        //     "player_hp": 46155,
        //     "player_hp_percentage": 100,
        //     "opponent_hp": 0,
        //     "opponent_hp_percentage": 0,
        //     "damage_given_to_opponent": 20521,
        //     "damage_given_to_player": 0,
        //     "confirm_button_text": "",
        //     "show_confirm_button": false,
        //     "rewards": [
        //     "<img src='\/img\/icons\/S_Light01.png' class='h-4 relative bottom-0.5'>70,896 EXP",
        //     "<img src='\/img\/icons\/I_GoldCoin.png' class='h-4 relative bottom-0.5'>2,270  Gold",
        //     "<img alt='Jewel (Legacy)' src='\/img\/icons\/two\/32px\/Gem_32.png' class='h-6 mr-2''> <span class='common-item' onclick='retrieveItem(11602, \"6898414896f60\")' id='item-id-6898414896f60'>Jewel (Legacy)<\/span>"
        //         ]
        // }

        var response = await request.ResponseAsync();
        if (response is null) return [];

        var attackResponse = await response.JsonAsync<AttackResponseBody?>();

        if (attackResponse?.Rewards is null)
        {
            return [];
        }

        List<ItemWithRarity> items = [];

        foreach (var reward in attackResponse.Rewards)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(reward);

            HtmlNode? span = doc.DocumentNode.SelectSingleNode("//span");

            if (span is null) continue;

            string rarityClass = Utils.UnmapClassToRarity(span.GetAttributeValue("class", "unknown"));
            var itemName = span.InnerText.Trim();
            var item = new ItemWithRarity(rarityClass, itemName);

            items.Add(item);
        }

        return items;
    }

    private const string ItemRarityLocatorPattern =
        ".common-item, .uncommon-item, .elite-item, .rare-item, .epic-item, .legendary-item, .celestial-item, .exotic-item";

    private async Task HandleNotABotPage(IPage page, ILocator iamNotABotButtonLocator)
    {
        if (await iamNotABotButtonLocator.Exists())
        {
            var iamNotABotButton = await iamNotABotButtonLocator.FindFirstNonHidden();

            if (iamNotABotButton is not null)
            {
                _logger.LogWarning("Detected 'I am not a bot' button");

                _mmoMetrics.IncrementCaptcha();

                var waitForPageTask = page.Context.WaitForPageAsync(new () { Timeout = 0 });
                await iamNotABotButton.ClickAsync();

                var captchaPage = await waitForPageTask;

                var cts = new CancellationTokenSource();
                _ = Task.Run(() =>
                {
                    // ReSharper disable once AccessToDisposedClosure
                    while (!cts.IsCancellationRequested)
                    {
#pragma warning disable CA1416
                        SystemSounds.Exclamation.Play();
                        Thread.Sleep(500);
#pragma warning restore CA1416
                    }
                }, cts.Token);
                await captchaPage.Locator(".swal2-animate-success-icon")
                    .WaitForAsync(new () { Timeout = 0 });
                await cts.CancelAsync();
                cts.Dispose();
                await captchaPage.CloseAsync();
                await page.ReloadAsync();
            }
        }
    }

    private async Task HandleTravelPage(
        ILocator travelHeadingLocator,
        ILocator stepPageAttackButtonLocator,
        ILocator stepPageMineButtonLocator,
        ILocator itemRarityLocator,
        ILocator stepPageChopButtonLocator,
        ILocator stepPageSalvageButtonLocator,
        ILocator stepPageCatchButtonLocator,
        ILocator stepPageStepButtonLocator
    )
    {
        var headingText = await travelHeadingLocator.TextContentAsync() ?? "Unknown";
        
        if (await stepPageAttackButtonLocator.Exists())
        {
            _mmoMetrics.IncrementAttacks(headingText);
            var attackButton = await stepPageAttackButtonLocator.FindFirstNonHidden();
            if (attackButton is not null)
            {
                await attackButton.ClickAsync();
            }
        }
        else if (await stepPageMineButtonLocator.Exists())
        {
            _mmoMetrics.IncrementResources("mine");

            var item = await itemRarityLocator.TryGetItem();

            if (item.HasValue)
            {
                _logger.LogWarning("Found resource: {ItemType} - {ItemId}", item.Value.Rarity, headingText);
                _mmoMetrics.IncrementResourcesStats(item.Value.Rarity, headingText);
            }

            var mineButton = await stepPageMineButtonLocator.FindFirstNonHidden();
            if (mineButton is not null)
            {
                await mineButton.ClickAsync();
            }
        }
        else if (await stepPageChopButtonLocator.Exists())
        {
            _mmoMetrics.IncrementResources("chop");

            var item = await itemRarityLocator.TryGetItem();

            if (item.HasValue)
            {
                _logger.LogWarning("Found resource: {ItemType} - {ItemId}", item.Value.Rarity, headingText);
                _mmoMetrics.IncrementResourcesStats(item.Value.Rarity, headingText);
            }

            var chopButton = await stepPageChopButtonLocator.FindFirstNonHidden();
            if (chopButton is not null)
            {
                await chopButton.ClickAsync();
            }
        }
        else if (await stepPageSalvageButtonLocator.Exists())
        {
            _mmoMetrics.IncrementResources("salvage");

            var item = await itemRarityLocator.TryGetItem();

            if (item.HasValue)
            {
                _logger.LogWarning("Found resource: {ItemType} - {ItemId}", item.Value.Rarity, headingText);
                _mmoMetrics.IncrementResourcesStats(item.Value.Rarity, headingText);
            }

            var salvageButton = await stepPageSalvageButtonLocator.FindFirstNonHidden();
            if (salvageButton is not null)
            {
                await salvageButton.ClickAsync();
            }
        }
        else if (await stepPageCatchButtonLocator.Exists())
        {
            _mmoMetrics.IncrementResources("catch");

            var item = await itemRarityLocator.TryGetItem();

            if (item.HasValue)
            {
                _logger.LogWarning("Found resource: {ItemType} - {ItemId}", item.Value.Rarity, headingText);
                _mmoMetrics.IncrementResourcesStats(item.Value.Rarity, headingText);
            }

            var catchButton = await stepPageCatchButtonLocator.FindFirstNonHidden();
            if (catchButton is not null)
            {
                await catchButton.ClickAsync();
            }
        }
        else if (await stepPageStepButtonLocator.Exists())
        {
            var stepType = headingText switch
            {
                "You have found an item!" => "item",
                "You take a step..." => "nothing",
                "The start of your adventure..." => "start",
                _ => "Unknown",
            };

            if (stepType == "item")
            {
                var item = await itemRarityLocator.TryGetItem();
                if (item.HasValue)
                {
                    _logger.LogWarning("Found item: {ItemType} - {ItemId}", item.Value.Rarity, item.Value.Name);
                    _mmoMetrics.IncrementItems(item.Value.Rarity, item.Value.Name);
                }
                else
                {
                    _logger.LogError("Failed to find item in step with item");
                }
            }

            _mmoMetrics.IncrementSteps(stepType);

            // page is weird, it has multiple buttons, some of which are enclosed by hidden divs.
            // we need to find the first button that is not enclosed by hidden div, that one is visible
            var stepButton = await stepPageStepButtonLocator.FindNonEnclosedByHiddenDiv();

            if (stepButton is not null)
            {
                var disabledAttribute = await stepButton.GetAttributeAsync("disabled");
                if (disabledAttribute is not null)
                {
                    await stepButton.WaitForElementStateAsync(ElementState.Enabled);
                }

                await stepButton.ClickAsync();
            }
        }
    }

    private async Task HandleAttackPage(ILocator attackPageAttackButtonLocator, ILocator attackPageLeaveButtonLocator)
    {
        var attackButton = await attackPageAttackButtonLocator.FindFirstNonHidden();

        if (attackButton is null)
        {
            var closePageButton = await attackPageLeaveButtonLocator.FindFirstNonHidden();

            if (closePageButton is not null)
            {
                await closePageButton.ClickAsync();
            }
        }
        else
        {
            // when attack finishes, button is still present, but it is enclosed by hidden div
            if (await attackButton.IsEnclosedByHiddenDiv())
            {
                var closePageButton = await attackPageLeaveButtonLocator.FindFirstNonHidden();

                if (closePageButton is not null)
                {
                    await closePageButton.ClickAsync();
                }
            }
            else
            {
                var disabledAttribute = await attackButton.GetAttributeAsync("disabled");
                if (disabledAttribute is not null)
                {
                    await attackButton.WaitForElementStateAsync(ElementState.Enabled);
                }

                await attackButton.ClickAsync(new ElementHandleClickOptions() { Timeout = 100000 });
                _mmoMetrics.IncrementAttackClicks();
            }
        }
    }

    private async Task HandleGatherPage(ILocator itemRarityLocator,
        ILocator gatherPageCraftingButtonLocator, 
        ILocator gatherPageCloseButtonLocator
    )
    {
        var item = await itemRarityLocator.TryGetItem();

        var craftingButton = await gatherPageCraftingButtonLocator.FindFirstNonHidden();

        if (craftingButton is null)
        {
            var closePageButton = await gatherPageCloseButtonLocator.FindFirstNonHidden();

            if (closePageButton is not null)
            {
                await closePageButton.ClickAsync();
            }
        }
        else
        {
            var disabledAttribute = await craftingButton.GetAttributeAsync("disabled");
            if (disabledAttribute is not null)
            {
                await craftingButton.WaitForElementStateAsync(ElementState.Enabled);
            }

            await craftingButton.ClickAsync();
            if (item.HasValue)
            {
                _mmoMetrics.IncrementResourceClicks(item.Value.Rarity, item.Value.Name);
            }
        }
    }

    private async Task MainLoop(IPage page, CancellationToken cancellationToken)
    {
        page.RequestFinished += async (_, request) =>
        {
            if (request.Url.StartsWith("https://web.simple-mmo.com/api/npcs/attack"))
            {
                var attackRewards = await GetAttackRewards(request);

                if (attackRewards.Count > 0)
                {
                    foreach (var attackReward in attackRewards)
                    {
                        _logger.LogWarning("Found item from attack: {ItemType} - {ItemName}", attackReward.Rarity,
                            attackReward.Name);
                        _mmoMetrics.IncrementItems(attackReward.Rarity, attackReward.Name);
                    }
                }
                else
                {
                    _logger.LogWarning("Attack has no rewards. Skipping...");
                }
            }

            if (request.Url.StartsWith("https://api.simple-mmo.com/api/action/travel/4"))
            {
            }
        };

        // allocate locators once
        var itemRarityLocator = page.Locator(ItemRarityLocatorPattern);
        var iamNotABotButtonLocator = page.Locator("a[href^='/i-am-not-a-bot']");
        var stepPageStepButtonLocator = page.Locator("[id^='step_btn']");
        var stepPageAttackButtonLocator = page.Locator("a:has-text('Attack')");
        var stepPageMineButtonLocator = page.Locator("button:has-text('Mine')");
        var stepPageChopButtonLocator = page.Locator("button:has-text('Chop')");
        var stepPageSalvageButtonLocator = page.Locator("button:has-text('Salvage')");
        var stepPageCatchButtonLocator = page.Locator("button:has-text('Catch')");
        var travelHeadingLocator = page.Locator("[x-text='travel.heading']");

        var attackPageAttackButtonLocator = page.Locator("button:has-text('Attack')");
        var attackPageLeaveButtonLocator = page.Locator("button:has-text('Leave')");

        var gatherPageCraftingButtonLocator = page.Locator("[id^='crafting_button']");
        var gatherPageCloseButtonLocator = page.Locator("button:has-text('Press here to close')");

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_config.IsEnabled)
            {
                _logger.LogWarning("Worker is disabled. Waiting for it to be enabled...");
                await Task.Delay(1000, cancellationToken);
                continue;
            }

            // wait some time to allow page to load
            await page.WaitForTimeoutAsync(1000);

            // regardless of page, check for bot
            await HandleNotABotPage(page, iamNotABotButtonLocator);

            if (page.Url.StartsWith("https://web.simple-mmo.com/travel"))
            {
                await HandleTravelPage(
                    travelHeadingLocator,
                    stepPageAttackButtonLocator,
                    stepPageMineButtonLocator,
                    itemRarityLocator,
                    stepPageChopButtonLocator,
                    stepPageSalvageButtonLocator,
                    stepPageCatchButtonLocator,
                    stepPageStepButtonLocator
                );
            }
            else if (page.Url.StartsWith("https://web.simple-mmo.com/npcs/attack"))
            {
                await HandleAttackPage(attackPageAttackButtonLocator, attackPageLeaveButtonLocator);
            }
            else if (page.Url.StartsWith("https://web.simple-mmo.com/crafting/material/gather"))
            {
                await HandleGatherPage(itemRarityLocator, gatherPageCraftingButtonLocator, gatherPageCloseButtonLocator);
            }
            else
            {
                _logger.LogError("Unknown page. Current URL: {Url}", page.Url);
                Console.Beep();
                await page.WaitForTimeoutAsync(5000);
            }
        }
    }

    private async Task Auth(IPage page)
    {
        if (File.Exists("cookies.json"))
        {
            var cookiesJson = await File.ReadAllTextAsync("cookies.json");
            var cookies = JsonSerializer.Deserialize<IEnumerable<Cookie>>(cookiesJson);

            if (cookies != null)
            {
                await page.Context.AddCookiesAsync(cookies);
            }
        }
        else
        {
            // Navigate to the main page
            await page.GotoAsync("https://web.simple-mmo.com/travel");

            // wait for some time
            await page.WaitForTimeoutAsync(300);

            // check if we are not logged in
            if (page.Url != "https://web.simple-mmo.com/login/credentials")
            {
                throw new Exception(
                    "Unexpected URL after loading auth page. Expected 'https://web.simple-mmo.com/login/credentials', but got: " +
                    page.Url);
            }

            await page.FillAsync("#email", _config.Login);
            await page.FillAsync("#password", _config.Password);

            await page.ClickAsync("button[type='submit']");

            if (page.Url == "https://web.simple-mmo.com/travel")
            {
                await SaveCookies(page);
                _mmoMetrics.IncrementAuthRequests();
            }
        }
    }


    private static async Task SaveCookies(IPage page)
    {
        var cookies = await page.Context.CookiesAsync();
        var convertedCookies = cookies.Select(x => new Cookie()
        {
            Domain = x.Domain,
            Expires = x.Expires,
            HttpOnly = x.HttpOnly,
            Name = x.Name,
            PartitionKey = x.PartitionKey,
            Path = x.Path,
            SameSite = x.SameSite,
            Secure = x.Secure,
            Value = x.Value
        });

        await File.WriteAllTextAsync("cookies.json", JsonSerializer.Serialize(convertedCookies));
    }
}

public static class LocatorExtensions
{
    public static async Task<bool> Exists(this ILocator locator)
    {
        return await locator.CountAsync() > 0;
    }

    public static async IAsyncEnumerable<IElementHandle> Enumerate(this ILocator locator)
    {
        var count = await locator.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var element = locator.Nth(i);
            yield return await element.ElementHandleAsync();
        }
    }

    public static async Task<IElementHandle?> FindNonEnclosedByHiddenDiv(this ILocator locator)
    {
        await foreach (var element in locator.Enumerate())
        {
            var isEnclosed = await element.IsEnclosedByHiddenDiv();
            if (!isEnclosed)
            {
                return element;
            }
        }

        return null;
    }

    public static async Task<bool> IsEnclosedByHiddenDiv(this IElementHandle element)
    {
        var parentElement = await element.EvaluateHandleAsync("b => b.parentElement");
        var isParentVisible = await parentElement.EvaluateAsync<string>(
            """
            el => {
                    return (window.getComputedStyle(el).opacity == 0 || window.getComputedStyle(el).display === 'none') ? '0' : '1';
                }
            """);

        return isParentVisible == "0";
    }

    public static async Task<IElementHandle?> FindFirstNonHidden(this ILocator locator)
    {
        await foreach (var element in locator.Enumerate())
        {
            string isVisible = await element.EvaluateAsync<string>(
                """
                el => {
                        return (window.getComputedStyle(el).opacity == 0 || window.getComputedStyle(el).display === 'none') ? '0' : '1';
                    }
                """);
            if (isVisible != "0")
            {
                return element;
            }
        }

        return null;
    }

    public static async Task<ItemWithRarity?> TryGetItem(this ILocator locator)
    {
        if (!await locator.Exists()) return null;

        var itemIdSpan = await locator.FindFirstNonHidden();
        if (itemIdSpan is null) return null;
        var itemType = Utils.UnmapClassToRarity(await itemIdSpan.GetAttributeAsync("class") ?? "Unknown");
        var itemId = await itemIdSpan.InnerTextAsync();

        return new ItemWithRarity(itemType, itemId);
    }
}

public static class Utils
{
    public static string UnmapClassToRarity(string attribute)
    {
        if (attribute.Contains("uncommon-item")) return "uncommon";
        if (attribute.Contains("common-item")) return "common";
        if (attribute.Contains("rare-item")) return "rare";
        if (attribute.Contains("elite-item")) return "elite";
        if (attribute.Contains("epic-item")) return "epic";
        if (attribute.Contains("legendary-item")) return "legendary";
        if (attribute.Contains("celestial-item")) return "celestial";
        if (attribute.Contains("exotic-item")) return "exotic";

        return "unknown";
    }
}