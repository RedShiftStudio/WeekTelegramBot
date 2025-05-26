using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    private static TelegramBotClient Bot;
    private static DateTime botStartTime;
    private static readonly string DataPath = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly Dictionary<long, UserSession> Sessions = new Dictionary<long, UserSession>();
    private static readonly Dictionary<string, List<Question>> TestQuestions = new Dictionary<string, List<Question>>();
    private static readonly List<SchoolPlace> SchoolPlaces = new List<SchoolPlace>();
    private static readonly Random Random = new Random();
    private static readonly HttpClient httpClient = new HttpClient();
    private static DateTime lastFactNotification = DateTime.MinValue;
    private static DateTime lastTestNotification = DateTime.MinValue;
    private static readonly TimeZoneInfo TargetTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
    private const string MistralApiKey = "XGZtOSCR1zqgHLW5eXXJrx9SIMwCgEI5";
    private const string TelegramBotToken = "8060806915:AAG9AaAllYUX0oYCFbENS03h022pjiqMG0g";
    private static bool isRunning = true;
    private static ChatId AdminChatId = 1621225477;
    //static ChatId AdminChatId = 0;

    static async Task Main()
    {
        botStartTime = DateTime.UtcNow;
        Directory.CreateDirectory(DataPath);
        LoadUsers();
        LoadLastCompletion();
        LoadTestQuestions();
        LoadSchoolPlaces();

        InitializeBot();
        StartDailyResetTimer();

        _ = Task.Run(MonitorDailyNotifications);

        Console.WriteLine("Бот запущен. Нажмите Enter для выхода...");
        Console.CancelKeyPress += (s, e) => isRunning = false;

        while (isRunning)
        {
            await Task.Delay(1000);
        }

        SaveAllData();
        Bot.StopReceiving();
    }

    static async Task MonitorDailyNotifications()
    {
        while (isRunning)
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TargetTimeZone);

            if (now.Hour == 8 && now.Minute == 0 && now.Second < 60)
            {
                if (lastTestNotification.Date < now.Date)
                {
                    await SendDailyTestsNotification();
                    lastTestNotification = now;
                }
            }

            if (now.Hour == 10 && now.Minute == 0 && now.Second < 60)
            {
                if (lastFactNotification.Date < now.Date)
                {
                    await SendDailyFactsNotification();
                    lastFactNotification = now;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    static async Task SendDailyTestsNotification()
    {
        var chatIds = Sessions.Keys.ToList();
        foreach (var chatId in chatIds)
        {
            try
            {
                await Bot.SendTextMessageAsync(chatId,
                    "Доброе утро! Новые тесты доступны!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка уведомления для {chatId}: {ex.Message}");
            }
        }
    }

    static async Task SendDailyFactsNotification()
    {
        var chatIds = Sessions.Keys.ToList();
        foreach (var chatId in chatIds)
        {
            try
            {
                var facts = new List<string>();
                foreach (var subject in new[] { "Химия", "Биология", "География" })
                {
                    var fact = await GetInterestingFact(subject);
                    facts.Add($"*{subject}:* {fact}");
                }

                await Bot.SendTextMessageAsync(chatId,
                    "Интересные факты дня:\n\n" + string.Join("\n\n", facts),
                    parseMode: ParseMode.Markdown);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка фактов для {chatId}: {ex.Message}");
            }
        }
    }

    static void InitializeBot()
    {
        Bot = new TelegramBotClient(TelegramBotToken);
        Bot.OnMessage += Bot_OnMessage;
        Bot.OnCallbackQuery += Bot_OnCallbackQuery;
        Bot.StartReceiving();
    }

    static void LoadUsers()
    {
        var usersFile = Path.Combine(DataPath, "users.txt");
        if (!System.IO.File.Exists(usersFile)) return;

        foreach (var line in System.IO.File.ReadAllLines(usersFile))
        {
            var parts = line.Split(';');
            if (parts.Length < 6 || !long.TryParse(parts[0], out var chatId)) continue;

            var session = new UserSession
            {
                ChatId = chatId,
                Name = parts[1],
                Surname = parts[2],
                Class = parts[3],
                DailyPlaceAttempts = int.Parse(parts[4]),
                LastPlaceAttemptDate = DateTime.ParseExact(parts[5], "dd.MM.yyyy", CultureInfo.InvariantCulture)
            };

            Sessions[chatId] = session;
        }
    }

    static void LoadLastCompletion()
    {
        var completionFile = Path.Combine(DataPath, "last_completion.txt");
        if (!System.IO.File.Exists(completionFile)) return;

        foreach (var line in System.IO.File.ReadAllLines(completionFile))
        {
            var parts = line.Split(';');
            if (parts.Length != 3 || !long.TryParse(parts[0], out var chatId)) continue;

            if (!Sessions.ContainsKey(chatId))
                Sessions[chatId] = new UserSession { ChatId = chatId };

            var session = Sessions[chatId];
            var subject = parts[1];
            var date = DateTime.ParseExact(parts[2], "dd.MM.yyyy", CultureInfo.InvariantCulture);

            session.LastCompletion[subject] = date;
        }
    }

    static void LoadTestQuestions()
    {
        var subjects = new[] { "chemistry", "geography", "biology" };
        foreach (var subject in subjects)
        {
            try
            {
                var qPath = Path.Combine(DataPath, $"{subject}_questions.txt");
                var aPath = Path.Combine(DataPath, $"{subject}_answers.txt");

                var questions = System.IO.File.ReadAllLines(qPath);
                var answers = System.IO.File.ReadAllLines(aPath);

                var list = new List<Question>();
                for (int i = 0; i < questions.Length; i++)
                {
                    var parts = answers[i].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;

                    var options = parts.OrderBy(x => Random.Next()).ToList();
                    var correctIndex = options.IndexOf(parts[0]);

                    list.Add(new Question
                    {
                        Text = questions[i],
                        Options = options,
                        CorrectIndex = correctIndex
                    });
                }

                TestQuestions[subject] = list;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка загрузки {subject}: {ex.Message}");
            }
        }
    }

    static void LoadSchoolPlaces()
    {
        var placesFile = Path.Combine(DataPath, "school_places.txt");
        if (!System.IO.File.Exists(placesFile)) return;

        SchoolPlaces.AddRange(System.IO.File.ReadAllLines(placesFile)
            .Select(line =>
            {
                var parts = line.Split(';');
                return new SchoolPlace { Name = parts[0], Clue = parts[1] };
            }));
    }

    static void SaveAllData()
    {
        SaveUsers();
        SaveLastCompletion();
    }

    static void SaveUsers()
    {
        var usersFile = Path.Combine(DataPath, "users.txt");
        var lines = Sessions.Values.Select(u =>
            $"{u.ChatId};{u.Name};{u.Surname};{u.Class};{u.DailyPlaceAttempts};{u.LastPlaceAttemptDate:dd.MM.yyyy}");
        System.IO.File.WriteAllLines(usersFile, lines);
    }

    static void SaveLastCompletion()
    {
        var completionFile = Path.Combine(DataPath, "last_completion.txt");
        var lines = Sessions.Values.SelectMany(u =>
            u.LastCompletion.Select(lc => $"{u.ChatId};{lc.Key};{lc.Value:dd.MM.yyyy}"));
        System.IO.File.WriteAllLines(completionFile, lines);
    }

    static async void Bot_OnMessage(object sender, MessageEventArgs e)
    {
        if (e.Message.Date < botStartTime)
        {
            return; // игнорируем старые сообщения
        }

        if (e.Message.Type != MessageType.Text) return;

        var chatId = e.Message.Chat.Id;
        //if (AdminChatId.Equals(chatId))
        //{
        //    // Пересылаем сообщение всем пользователям, кроме админа
        //    foreach (var user in Sessions.Keys.ToList())
        //    {
        //        if (user != AdminChatId)
        //        {
        //            try
        //            {
        //                await Bot.ForwardMessageAsync(
        //                    chatId: user,
        //                    fromChatId: chatId,
        //                    messageId: e.Message.MessageId
        //                );
        //            }
        //            catch (Exception ex)
        //            {  
        //                Console.WriteLine($"Ошибка пересылки к {user}: {ex.Message}");
        //            }
        //        }
        //    }
        //    return; // Прерываем обработку, чтобы сообщение админа не вызывало другие действия
        //}
        //else
        //{
            //chatId = e.Message.Chat.Id;
            var message = e.Message.Text.Trim();

            if (!Sessions.ContainsKey(chatId)) 
                Sessions[chatId] = new UserSession { ChatId = chatId };

            var session = Sessions[chatId];

            switch (session.State)
            {
                case SessionState.Start:
                    if (message == "/start")
                    {
                        session.State = SessionState.Registration;
                        await Bot.SendTextMessageAsync(chatId, "Введите ваше имя:");
                    }
                    break;

                case SessionState.Registration:
                    switch (session.RegistrationStep)
                    {
                        case 0:
                            session.Name = message;
                            session.RegistrationStep++;
                            await Bot.SendTextMessageAsync(chatId, "Введите фамилию:");
                            break;
                        case 1:
                            session.Surname = message;
                            session.RegistrationStep++;
                            await Bot.SendTextMessageAsync(chatId, "Введите класс:");
                            break;
                        case 2:
                            session.Class = message;
                            session.RegistrationStep = 0;
                            session.State = SessionState.MainMenu;
                            SaveUser(session);
                            await ShowMainMenu(chatId);
                            break;
                    }
                    break;

                case SessionState.MainMenu:
                    if (message.StartsWith("/")) break;
                    await HandleUserQuestion(chatId, message);
                    break;

                case SessionState.Asking:
                    await HandleUserQuestion(chatId, message);
                    break;

                case SessionState.GuessingPlace:
                    await CheckPlaceGuess(chatId, message);
                    break;
            }
        //}

        
    }

    

static async void Bot_OnCallbackQuery(object sender, CallbackQueryEventArgs e)
    {
        var callback = e.CallbackQuery;
        var chatId = callback.Message.Chat.Id;

        if (!Sessions.TryGetValue(chatId, out var session))
        {
            await Bot.SendTextMessageAsync(chatId, "Сначала зарегистрируйтесь через /start");
            return;
        }

        switch (callback.Data)
        {
            case "back_main":
                session.State = SessionState.MainMenu;
                await ShowMainMenu(chatId);
                break;

            case "back_tests":
                session.State = SessionState.MainMenu;
                await ShowTestsMenu(chatId);
                break;

            case "back_places":
                session.State = SessionState.PlacesMenu;
                await ShowPlacesMenu(chatId);
                break;

            default:
                if (callback.Data.StartsWith("mode_"))
                {
                    var mode = callback.Data.Split('_')[1];

                    if (mode == "tests")
                        session.CurrentMode = Mode.Tests;
                    else if (mode == "places")
                        session.CurrentMode = Mode.Places;
                    else if (mode == "ask")
                        session.CurrentMode = Mode.Ask;
                    else
                        session.CurrentMode = Mode.Tests;

                    session.State = SessionState.MainMenu;

                    switch (session.CurrentMode)
                    {
                        case Mode.Tests:
                            await ShowTestsMenu(chatId);
                            break;
                        case Mode.Places:
                            await ShowPlacesMenu(chatId);
                            break;
                        case Mode.Ask:
                            await Bot.SendTextMessageAsync(chatId, "Введите ваш вопрос:");
                            session.State = SessionState.Asking;
                            break;
                    }
                }
                else if (callback.Data.StartsWith("subject_"))
                {
                    var subject = callback.Data.Split('_')[1];
                    if (CanTakeTest(chatId, subject))
                        await StartTest(chatId, subject);
                    else
                        await Bot.SendTextMessageAsync(chatId, $"Тест по {subject} доступен раз в день");
                }
                else if (callback.Data.StartsWith("place_"))
                {
                    if (session.DailyPlaceAttempts >= 5)
                    {
                        await Bot.SendTextMessageAsync(chatId, "Все попытки использованы!");
                        return;
                    }

                    var placeIndex = int.Parse(callback.Data.Split('_')[1]);
                    await ShowPlaceClue(chatId, placeIndex);
                }
                else if (callback.Data.StartsWith("answer_"))
                {
                    var parts = callback.Data.Split('_');
                    var subject = parts[1];
                    var questionIndex = int.Parse(parts[2]);
                    var answerIndex = int.Parse(parts[3]);

                    if (session.CurrentTest == null || session.CurrentTest.Subject != subject)
                        return;

                    var currentQuestion = session.CurrentTest.Questions[questionIndex];
                    if (currentQuestion.Answered) return;

                    currentQuestion.Answered = true;
                    currentQuestion.UserAnswer = answerIndex;

                    if (answerIndex == currentQuestion.CorrectIndex)
                        session.CurrentTest.CorrectAnswers++;

                    if (session.CurrentTest.Questions.All(q => q.Answered))
                        await FinishTest(chatId, subject);
                    else
                        await SendNextQuestion(chatId, subject);
                }
                break;
        }
    }

    static async Task ShowMainMenu(long chatId)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🧪 Тесты", "mode_tests") },
            new[] { InlineKeyboardButton.WithCallbackData("🏫 места", "mode_places") },
            new[] { InlineKeyboardButton.WithCallbackData("🤖 Задать вопрос", "mode_ask") }
        });

        await Bot.SendTextMessageAsync(chatId, "Выберите режим:", replyMarkup: keyboard);
    }

    static async Task ShowTestsMenu(long chatId)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Химия", "subject_chemistry") },
            new[] { InlineKeyboardButton.WithCallbackData("География", "subject_geography") },
            new[] { InlineKeyboardButton.WithCallbackData("Биология", "subject_biology") },
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_main") }
        });

        await Bot.SendTextMessageAsync(chatId, "Выберите предмет для теста:", replyMarkup: keyboard);
    }

    static async Task ShowPlacesMenu(long chatId)
    {
        var session = Sessions[chatId];
        if (session.DailyPlaceAttempts >= 5)
        {
            await Bot.SendTextMessageAsync(chatId, "Все 5 попыток использованы!");
            await ShowMainMenu(chatId);
            return;
        }

        var availablePlaces = GetAvailablePlaces(chatId);
        var buttons = availablePlaces.Select((p, i) =>
            InlineKeyboardButton.WithCallbackData($"Место {i + 1}", $"place_{i}"))
            .ToArray();

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            buttons,
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_main") }
        });

        await Bot.SendTextMessageAsync(chatId, "Выберите место:", replyMarkup: keyboard);
    }

    static async Task ShowPlaceClue(long chatId, int index)
    {
        var session = Sessions[chatId];
        var availablePlaces = GetAvailablePlaces(chatId);

        if (index >= availablePlaces.Count || session.DailyPlaceAttempts >= 5)
        {
            await Bot.SendTextMessageAsync(chatId, "Место не доступно");
            return;
        }

        var place = availablePlaces[index];
        session.CurrentPlace = place;
        session.State = SessionState.GuessingPlace;
        session.DailyPlaceAttempts++;

        SaveUser(session);

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_places") }
        });

        await Bot.SendTextMessageAsync(chatId, $"Подсказка: {place.Clue}", replyMarkup: keyboard);
        await Bot.SendTextMessageAsync(chatId, "Введите ответ:");
    }

    static void SaveUser(UserSession user)
    {
        var usersFile = Path.Combine(DataPath, "users.txt");
        var lines = System.IO.File.Exists(usersFile)
            ? System.IO.File.ReadAllLines(usersFile).ToList()
            : new List<string>();

        var existingLineIndex = lines.FindIndex(l => l.StartsWith($"{user.ChatId};"));
        var newLine = $"{user.ChatId};{user.Name};{user.Surname};{user.Class};{user.DailyPlaceAttempts};{user.LastPlaceAttemptDate:dd.MM.yyyy}";

        if (existingLineIndex >= 0)
        {
            lines[existingLineIndex] = newLine;
        }
        else
        {
            lines.Add(newLine);
        }

        System.IO.File.WriteAllLines(usersFile, lines);
    }

    static List<SchoolPlace> GetAvailablePlaces(long chatId)
    {
        var session = Sessions[chatId];
        if (session.DailyPlaceAttempts >= 5)
            return new List<SchoolPlace>();

        return SchoolPlaces
            .OrderBy(p => Random.Next())
            .Take(5)
            .ToList();
    }

    static async Task CheckPlaceGuess(long chatId, string guess)
    {
        var session = Sessions[chatId];
        if (session.CurrentPlace == null)
        {
            await Bot.SendTextMessageAsync(chatId, "Ошибка. Выберите место снова.");
            await ShowPlacesMenu(chatId);
            return;
        }

        if (guess.Equals(session.CurrentPlace.Name, StringComparison.OrdinalIgnoreCase))
        {
            LogPlaceResult(chatId, session.CurrentPlace.Name);
            await Bot.SendTextMessageAsync(chatId, $"Правильно! Это {session.CurrentPlace.Name}");
        }
        else
        {
            await Bot.SendTextMessageAsync(chatId, "Неверно. Попробуйте снова.");
        }

        session.CurrentPlace = null;

        if (session.DailyPlaceAttempts >= 5)
        {
            await Bot.SendTextMessageAsync(chatId, "Все попытки использованы!");
            await ShowMainMenu(chatId);
        }
        else
        {
            await ShowPlacesMenu(chatId);
        }
    }

    static async Task HandleUserQuestion(long chatId, string question)
    {
        try
        {
            Console.WriteLine($"Получен вопрос от {chatId}: {question}");

            var subject = await DetectSubjectWithMistral(question);
            Console.WriteLine($"Определенный предмет: {subject ?? "Не определен"}");

            if (subject == null)
            {
                await Bot.SendTextMessageAsync(chatId, "Вопрос не относится к химии/биологии/географии");
                return;
            }

            var answer = await GetMistralAnswer(question, subject);
            Console.WriteLine($"Ответ от Mistral: {answer}");

            await Bot.SendTextMessageAsync(chatId, $"[{subject}] Ответ: {answer}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обработки вопроса: {ex.Message}");
            await Bot.SendTextMessageAsync(chatId, "Произошла ошибка. Попробуйте позже.");
        }
    }

    static async Task<string> DetectSubjectWithMistral(string question)
    {
        var apiUrl = "https://api.mistral.ai/v1/chat/completions";
        var requestBody = new
        {
            model = "mistral-tiny",
            messages = new[]
            {
                new { role = "system", content = "Ты определяешь предмет вопроса. Отвечай только 'Химия', 'Биология', 'География' или 'Нет'." },
                new { role = "user", content = $"К какому предмету относится вопрос: {question}?" }
            },
            temperature = 0.1,
            max_tokens = 10
        };

        try
        {
            var responseBody = await CallMistralApi(apiUrl, requestBody);
            Console.WriteLine($"Mistral Response: {responseBody}");

            var result = JsonSerializer.Deserialize<MistralResponse>(responseBody);

        
            if (result?.Choices == null || !result.Choices.Any())
                return null;

            var firstChoice = result.Choices[0];
            if (firstChoice?.Message?.Content == null)
                return null;

            var detectedSubject = firstChoice.Message.Content.Trim();
            var validSubjects = new[] { "Химия", "Биология", "География" };

            return validSubjects.Contains(detectedSubject) ? detectedSubject : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка определения предмета: {ex.Message}");
            return null;
        }
    }

    static async Task<string> GetMistralAnswer(string question, string subject)
    {
        var apiUrl = "https://api.mistral.ai/v1/chat/completions";
        var requestBody = new
        {
            model = "mistral-medium",
            messages = new[]
            {
                new { role = "system", content = $"Ты помощник по {subject}. Отвечай кратко и понятно." },
                new { role = "user", content = question }
            },
            temperature = 0.7,
            max_tokens = 500
        };

        try
        {
            var responseBody = await CallMistralApi(apiUrl, requestBody);
            Console.WriteLine($"Mistral Answer Response: {responseBody}");

            var result = JsonSerializer.Deserialize<MistralResponse>(responseBody);

            if (result?.Choices == null || !result.Choices.Any())
                return "Ошибка обработки ответа";

            var firstChoice = result.Choices[0];
            if (firstChoice?.Message?.Content == null)
                return "Ошибка контента";

            return firstChoice.Message.Content.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка генерации ответа: {ex.Message}");
            return "Произошла ошибка";
        }
    }

    static async Task<string> CallMistralApi(string apiUrl, object requestBody)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            var content = JsonContent.Create(requestBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MistralApiKey);

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode(); 

            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HTTP error: {ex.Message}");
            throw new Exception("Ошибка API");
        }
    }

    static bool CanTakeTest(long chatId, string subject)
    {
        var session = Sessions[chatId];
        return !session.LastCompletion.ContainsKey(subject) ||
               session.LastCompletion[subject].Date < DateTime.Today;
    }

    static async Task StartTest(long chatId, string subject)
    {
        var session = Sessions[chatId];
        session.LastCompletion[subject] = DateTime.Now;

        var questions = TestQuestions[subject]
            .OrderBy(q => Random.Next())
            .Take(4)
            .Select(q => new TestQuestion
            {
                Text = q.Text,
                Options = q.Options,
                CorrectIndex = q.CorrectIndex,
                Answered = false
            })
            .ToList();

        session.CurrentTest = new TestSession
        {
            Subject = subject,
            Questions = questions,
            StartTime = DateTime.Now
        };

        await SendNextQuestion(chatId, subject);
    }

    //static async Task SendNextQuestion(long chatId, string subject)
    //{
    //    var session = Sessions[chatId];
    //    var currentQuestion = session.CurrentTest.Questions
    //        .FirstOrDefault(q => !q.Answered);

    //    if (currentQuestion == null)
    //    {
    //        await FinishTest(chatId, subject);
    //        return;
    //    }

    //    var index = session.CurrentTest.Questions.IndexOf(currentQuestion);
    //    var options = currentQuestion.Options.Select((o, i) =>
    //        InlineKeyboardButton.WithCallbackData(
    //            $"{(char)('A' + i)}. {o}",
    //            $"answer_{subject}_{index}_{i}"
    //        )).ToArray();

    //    var keyboard = new InlineKeyboardMarkup(new[]
    //    {
    //        options,
    //        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_tests") }
    //    });

    //    await Bot.SendTextMessageAsync(chatId,
    //        $"Вопрос {index + 1}/4:\n{currentQuestion.Text}",
    //        replyMarkup: keyboard);
    //}

    static async Task SendNextQuestion(long chatId, string subject)
    {
        var session = Sessions[chatId];
        var currentQuestion = session.CurrentTest.Questions
            .FirstOrDefault(q => !q.Answered);
        if (currentQuestion == null)
        {
            await FinishTest(chatId, subject);
            return;
        }

        int index = session.CurrentTest.Questions.IndexOf(currentQuestion);

        // Формируем текст вариантов ответов
        var optionsText = new System.Text.StringBuilder();
        for (int i = 0; i < currentQuestion.Options.Count; i++)
        {
            char letter = (char)('A' + i);
            optionsText.AppendLine($"{letter}. {currentQuestion.Options[i]}");
        }

        // Формируем сообщение с вопросом и текстовыми вариантами
        var messageText = $"Вопрос {index + 1}/4:\n" +
                          $"{currentQuestion.Text}\n\n" +
                          "Варианты ответов:\n" +
                          $"{optionsText.ToString()}";

        // Формируем кнопки
        var buttons = currentQuestion.Options.Select((o, i) =>
            InlineKeyboardButton.WithCallbackData(
                $"{(char)('A' + i)}. {o}",
                $"answer_{subject}_{index}_{i}"
            )).ToArray();

        var keyboard = new InlineKeyboardMarkup(new[]
        {
        buttons,
        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_tests") }
    });

        await Bot.SendTextMessageAsync(chatId, messageText, replyMarkup: keyboard);
    }

    static async Task FinishTest(long chatId, string subject)
    {
        var session = Sessions[chatId];
        var correct = session.CurrentTest.CorrectAnswers;

        if (correct == 4)
        {
            LogTestResult(chatId, subject);
            await Bot.SendTextMessageAsync(chatId,
                $"Поздравляем! Все 4 ответа правильные по {subject}!");
        }
        else
        {
            await Bot.SendTextMessageAsync(chatId,
                $"Правильных ответов: {correct}/4. Попробуйте завтра.");
        }

        session.CurrentTest = null;
        await ShowMainMenu(chatId);
    }

    static void LogTestResult(long chatId, string subject)
    {
        var session = Sessions[chatId];
        var logEntry = $"{DateTime.Now:dd.MM.yyyy};{session.Name};{session.Surname};{session.Class};{subject}";
        System.IO.File.AppendAllText(Path.Combine(DataPath, "results.txt"), logEntry + Environment.NewLine);
    }

    static void LogPlaceResult(long chatId, string placeName)
    {
        var session = Sessions[chatId];
        var logEntry = $"{DateTime.Now:dd.MM.yyyy};{session.Name};{session.Surname};{session.Class};{placeName}";
        System.IO.File.AppendAllText(Path.Combine(DataPath, "results.txt"), logEntry + Environment.NewLine);
    }

    static void StartDailyResetTimer()
    {
        var timer = new Timer(async _ =>
        {
            try
            {
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TargetTimeZone);
                foreach (var session in Sessions.Values)
                {
                    session.LastCompletion.Clear();
                    session.DailyPlaceAttempts = 0;
                    session.LastPlaceAttemptDate = now.Date;
                }
                SaveAllData();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка сброса: {ex.Message}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    static void StartNotificationTimers()
    {
        new Timer(async _ =>
        {
            try
            {
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TargetTimeZone);
                if (now.Hour == 8 && now.Minute == 0 && now.Second < 60)
                {
                    if (lastTestNotification.Date < now.Date)
                    {
                        foreach (var chatId in Sessions.Keys)
                        {
                            await Bot.SendTextMessageAsync(chatId,
                                "Доброе утро! Новые тесты доступны!");
                        }
                        lastTestNotification = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка таймера тестов: {ex.Message}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        new Timer(async _ =>
        {
            try
            {
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TargetTimeZone);
                if (now.Hour == 00 && now.Minute == 57 && now.Second < 60)
                {
                    if (lastFactNotification.Date < now.Date && Sessions.Any())
                    {
                        foreach (var chatId in Sessions.Keys)
                        {
                            try
                            {
                                var facts = new List<string>();
                                foreach (var subject in new[] { "Химия", "Биология", "География" })
                                {
                                    var fact = await GetInterestingFact(subject);
                                    facts.Add($"*{subject}:* {fact}");
                                }
                                await Bot.SendTextMessageAsync(chatId,
                                    "Интересные факты дня:\n\n" + string.Join("\n\n", facts),
                                    parseMode: ParseMode.Markdown);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Ошибка отправки фактов для {chatId}: {ex.Message}");
                            }
                        }
                        lastFactNotification = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка таймера фактов: {ex.Message}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    static async Task<string> GetInterestingFact(string subject)
    {
        var apiUrl = "https://api.mistral.ai/v1/chat/completions";
        var requestBody = new
        {
            model = "mistral-medium",
            messages = new[]
            {
                new {
                    role = "system",
                    content = $"Ты эксперт по {subject}. Сгенерируй краткий интересный факт для студента, избегай слово конечно, просто скажи факт."
                },
                new {
                    role = "user",
                    content = $"Расскажи интересный факт по {subject} для студента, избегай слово конечно, просто скажи факт"
                }
            },
            temperature = 0.7,
            max_tokens = 200
        };

        try
        {
            var responseBody = await CallMistralApi(apiUrl, requestBody);
            var result = JsonSerializer.Deserialize<MistralResponse>(responseBody);

            if (result?.Choices?[0]?.Message?.Content == null)
                return "Факт недоступен";

            return result.Choices[0].Message.Content.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка факта по {subject}: {ex.Message}");
            return "Ошибка генерации факта";
        }
    }
}



class UserSession
{
    public long ChatId { get; set; }
    public SessionState State { get; set; } = SessionState.Start;
    public Mode CurrentMode { get; set; } = Mode.Tests;
    public int RegistrationStep { get; set; }
    public string Name { get; set; }
    public string Surname { get; set; }
    public string Class { get; set; }
    public TestSession CurrentTest { get; set; }
    public SchoolPlace CurrentPlace { get; set; }
    public Dictionary<string, DateTime> LastCompletion { get; } = new Dictionary<string, DateTime>();
    public int DailyPlaceAttempts { get; set; }
    public DateTime LastPlaceAttemptDate { get; set; }
}

class TestSession
{
    public string Subject { get; set; }
    public List<TestQuestion> Questions { get; set; }
    public int CorrectAnswers { get; set; }
    public DateTime StartTime { get; set; }
}

class TestQuestion
{
    public string Text { get; set; }
    public List<string> Options { get; set; }
    public int CorrectIndex { get; set; }
    public bool Answered { get; set; }
    public int UserAnswer { get; set; }
}

class SchoolPlace
{
    public string Name { get; set; }
    public string Clue { get; set; }
}

class Question
{
    public string Text { get; set; }
    public List<string> Options { get; set; }
    public int CorrectIndex { get; set; }
}

public class MistralResponse
{
    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; }
}

public class Choice
{
    [JsonPropertyName("message")]
    public Message Message { get; set; }
}

public class Message
{
    [JsonPropertyName("content")]
    public string Content { get; set; }
}

enum SessionState
{
    Start,
    Registration,
    MainMenu,
    TestsMenu,
    PlacesMenu,
    GuessingPlace,
    Asking
}

enum Mode
{
    Tests,
    Places,
    Ask
}