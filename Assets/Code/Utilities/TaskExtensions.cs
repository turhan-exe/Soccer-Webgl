using System;
using System.Threading.Tasks;

namespace FStudio.Utilities {
    public static class TaskExtensions {
        public static async Task WithTimeout(this Task task, int milliseconds, string label = null) {
            var timeoutTask = Task.Delay(milliseconds);
            var completed = await Task.WhenAny(task, timeoutTask);
            if (completed == timeoutTask) {
                throw new TimeoutException($"Task timeout{(string.IsNullOrEmpty(label) ? string.Empty : $" ({label})")} after {milliseconds}ms");
            }
            await task; // propagate exceptions if any
        }

        public static async Task<T> WithTimeout<T>(this Task<T> task, int milliseconds, string label = null) {
            var timeoutTask = Task.Delay(milliseconds);
            var completed = await Task.WhenAny(task, timeoutTask);
            if (completed == timeoutTask) {
                throw new TimeoutException($"Task timeout{(string.IsNullOrEmpty(label) ? string.Empty : $" ({label})")} after {milliseconds}ms");
            }
            return await task; // propagate result and exceptions
        }
    }
}

