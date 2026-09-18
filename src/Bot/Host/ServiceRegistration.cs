using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Confessions;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Mood;
using Trivozhno.Features.Navigation;
using Trivozhno.Features.Reminders;
using Trivozhno.Features.Settings;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Stories;
using Trivozhno.Infrastructure.Telegram;
using Trivozhno.Resources;

namespace Trivozhno.Host;

public static class ServiceRegistration
{
    public static IServiceCollection AddBot(this IServiceCollection services, BotOptions options)
    {
        services.AddSingleton(options); services.AddSingleton<IClock, SystemClock>(); services.AddSingleton<Uk>(); services.AddSingleton<UserLocks>();
        services.AddSingleton<WorkerStatus>(); services.AddSingleton<AiQuota>(); services.AddSingleton<TelegramRateGate>();
        services.AddDbContext<BotDb>(b => b.UseNpgsql(ConnectionStrings.Parse(options.Database)).EnableSensitiveDataLogging(false).EnableDetailedErrors(false));
        // Suppress HttpClient URLs (Telegram embeds the secret in the path) and SQL payloads.
        services.AddLogging(b => { b.AddFilter("System.Net.Http.HttpClient", LogLevel.None); b.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None); });
        services.AddHttpClient<ITelegramClient, TelegramClient>(c => c.Timeout = TimeSpan.FromSeconds(40)).RemoveAllLoggers();
        services.AddHttpClient<IAiClient, GroqClient>(c => c.Timeout = Timeout.InfiniteTimeSpan).RemoveAllLoggers();
        services.AddHttpClient<IStorySource, SodaStorySource>(c => c.Timeout = TimeSpan.FromSeconds(12)).RemoveAllLoggers();
        services.AddScoped<IKnowledgeRetriever, KnowledgeRetriever>(); services.AddScoped<IConversationMemory, ConversationMemory>();
        services.AddScoped<BookImporter>(); services.AddScoped<Ui>(); services.AddScoped<DraftStore>(); services.AddScoped<Router>();
        services.AddScoped<ConversationHandler>(); services.AddScoped<ConfessionHandler>(); services.AddScoped<MoodHandler>();
        services.AddScoped<ReminderHandler>(); services.AddScoped<SettingsHandler>();
        services.AddSingleton<InboxProcessor>(); services.AddSingleton<AiProcessor>(); services.AddSingleton<OutboxProcessor>(); services.AddSingleton<Maintenance>();
        return services;
    }
}
