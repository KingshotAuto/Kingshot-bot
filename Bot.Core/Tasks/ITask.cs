using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Services;
using System.Threading.Tasks;
using System.Threading;

namespace Bot.Core.Tasks
{
    public interface ITask
    {
        TaskType TaskType { get; }
        string Name { get; }
        Task<TaskExecutionDetails> ExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default, bool isReRun = false, IUserNotificationService? userNotifications = null);
        Task InitializeAsync(LogService logger, CancellationToken cancellationToken = default);
    }
} 