using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WorkshopStatistics
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string configPath = "config.json";
            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Файл {configPath} не найден. Создайте его и укажите API ключ и User ID.");
                return;
            }

            var config = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(configPath));
            if (!config.ContainsKey("apiKey") || !config.ContainsKey("userId"))
            {
                Console.WriteLine($"Файл {configPath} должен содержать поля 'apiKey' и 'userId'.");
                return;
            }

            string apiKey = config["apiKey"];
            string userId = config["userId"];

            string workshopItemsPath = "workshop_items.txt";
            if (!File.Exists(workshopItemsPath))
            {
                Console.WriteLine($"Файл {workshopItemsPath} не найден. Создайте его и добавьте идентификаторы мастерских (по одному на строку).");
                return;
            }

            var workshopItems = File.ReadAllLines(workshopItemsPath).ToList();
            if (!workshopItems.Any())
            {
                Console.WriteLine($"Файл {workshopItemsPath} пуст. Добавьте идентификаторы мастерских.");
                return;
            }

            string dataFilePath = "previous_workshop_data.json";
            int monitoringInterval = 10000; // Интервал обновления мс

            var workshopInfo = await GetWorkshopInfo(apiKey, userId, workshopItems);
            Console.WriteLine($"Суммарные уникальные посетители: {workshopInfo.TotalViews}");
            Console.WriteLine($"Суммарные подписчики на всех работах: {workshopInfo.TotalSubscribers}");
            Console.WriteLine($"Добавили в избранное: {workshopInfo.TotalFavorited}");
            Console.WriteLine(new string('-', 40));

            Console.WriteLine($"Самая популярная работа по количеству уникальных посетителей: {workshopInfo.MostViewed.Title}");
            Console.WriteLine($"Самая популярная работа по количеству подписчиков: {workshopInfo.MostSubscribed.Title}");
            Console.WriteLine($"Самая популярная работа по добавлениям в избранное: {workshopInfo.MostFavorited.Title}");
            Console.WriteLine(new string('-', 40));

            var previousStats = LoadPreviousData(dataFilePath);

            Console.WriteLine("Начальная статистика (включая изменения с прошлого сеанса):");
            var currentStats = new Dictionary<string, WorkshopStats>();
            foreach (var workshopId in workshopItems)
            {
                var stats = await GetWorkshopStats(apiKey, workshopId);
                currentStats[workshopId] = stats;
                PrintWorkshopStats(stats, previousStats.ContainsKey(workshopId) ? previousStats[workshopId] : null);
            }
            SaveCurrentData(dataFilePath, currentStats);

            Console.WriteLine("Переход в режим мониторинга изменений...");

            while (true)
            {
                foreach (var workshopId in workshopItems)
                {
                    var currentStat = await GetWorkshopStats(apiKey, workshopId);
                    if (previousStats.ContainsKey(workshopId))
                    {
                        CheckForChanges(currentStat, previousStats[workshopId]);
                    }
                    previousStats[workshopId] = currentStat;
                }
                Thread.Sleep(monitoringInterval);
            }
        }

        static async Task<WorkshopInfo> GetWorkshopInfo(string apiKey, string userId, List<string> workshopItems)
        {
            int totalSubscribers = 0;
            int totalViews = 0;
            int totalFavorited = 0;

            var allStats = new List<WorkshopStats>();

            foreach (var workshopId in workshopItems)
            {
                var stats = await GetWorkshopStats(apiKey, workshopId);
                allStats.Add(stats);
                totalSubscribers += stats.Subscriptions;
                totalViews += stats.Views;
                totalFavorited += stats.Favorited;
            }

            var mostViewed = allStats.OrderByDescending(s => s.Views).First();
            var mostSubscribed = allStats.OrderByDescending(s => s.Subscriptions).First();
            var mostFavorited = allStats.OrderByDescending(s => s.Favorited).First();

            return new WorkshopInfo
            {
                TotalSubscribers = totalSubscribers,
                TotalViews = totalViews,
                TotalFavorited = totalFavorited,
                MostViewed = mostViewed,
                MostSubscribed = mostSubscribed,
                MostFavorited = mostFavorited
            };
        }

        static async Task<WorkshopStats> GetWorkshopStats(string apiKey, string workshopId)
        {
            string url = $"https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
            var values = new Dictionary<string, string>
            {
                { "key", apiKey },
                { "itemcount", "1" },
                { "publishedfileids[0]", workshopId }
            };

            using (var client = new HttpClient())
            using (var content = new FormUrlEncodedContent(values))
            {
                var response = await client.PostAsync(url, content);
                response.EnsureSuccessStatusCode();

                var jsonString = await response.Content.ReadAsStringAsync();
                var data = JObject.Parse(jsonString);

                var item = data["response"]["publishedfiledetails"]?[0];

                if (item != null)
                {
                    return new WorkshopStats
                    {
                        Title = item["title"]?.ToString(),
                        Views = item["views"]?.ToObject<int>() ?? 0,
                        Subscriptions = item["subscriptions"]?.ToObject<int>() ?? 0,
                        Favorited = item["favorited"]?.ToObject<int>() ?? 0
                    };
                }
                return null;
            }
        }

        static void PrintWorkshopStats(WorkshopStats current, WorkshopStats previous)
        {
            Console.WriteLine($"Название работы: {current.Title}");
            Console.WriteLine($"Уникальные посетители: {current.Views}");

            if (previous != null)
            {
                Console.WriteLine($"Новых уникальных посетителей с прошлого сеанса: {current.Views - previous.Views}");
                Console.WriteLine($"Новых подписчиков с прошлого сеанса: {current.Subscriptions - previous.Subscriptions}");
                Console.WriteLine($"Добавлено в избранное с прошлого сеанса: {current.Favorited - previous.Favorited}");
            }
            else
            {
                Console.WriteLine("Нет данных за прошлый сеанс.");
            }

            Console.WriteLine($"Подписчики: {current.Subscriptions}");
            Console.WriteLine($"Добавлено в избранное: {current.Favorited}");
            Console.WriteLine(new string('-', 40));
        }

        static void CheckForChanges(WorkshopStats current, WorkshopStats previous)
        {
            string currentTime = DateTime.Now.ToString("HH:mm:ss");
            if (current.Views > previous.Views)
            {
                Console.WriteLine($"[{currentTime}] Работу {current.Title} - посетил новый уникальный посетитель.");
            }
            if (current.Subscriptions > previous.Subscriptions)
            {
                Console.WriteLine($"[{currentTime}] На работу {current.Title} - подписался новый пользователь.");
            }
            if (current.Favorited > previous.Favorited)
            {
                Console.WriteLine($"[{currentTime}] Работу {current.Title} - добавили в избранное.");
            }
        }

        static void SaveCurrentData(string filePath, Dictionary<string, WorkshopStats> currentStats)
        {
            var jsonData = JsonConvert.SerializeObject(currentStats, Formatting.Indented);
            File.WriteAllText(filePath, jsonData);
        }

        static Dictionary<string, WorkshopStats> LoadPreviousData(string filePath)
        {
            if (File.Exists(filePath))
            {
                var jsonData = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<Dictionary<string, WorkshopStats>>(jsonData);
            }
            return new Dictionary<string, WorkshopStats>();
        }
    }

    class WorkshopStats
    {
        public string Title { get; set; }
        public int Views { get; set; }
        public int Subscriptions { get; set; }
        public int Favorited { get; set; }
    }

    class WorkshopInfo
    {
        public int TotalSubscribers { get; set; }
        public int TotalViews { get; set; }
        public int TotalFavorited { get; set; }
        public WorkshopStats MostViewed { get; set; }
        public WorkshopStats MostSubscribed { get; set; }
        public WorkshopStats MostFavorited { get; set; }
    }
}