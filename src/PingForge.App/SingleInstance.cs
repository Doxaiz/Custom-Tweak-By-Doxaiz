using System.Threading;

namespace PingForge.App;

/// <summary>
/// Один экземпляр программы — как у обычных настольных программ.
///
/// Зачем это нужно: человек дважды щёлкает по значку (или запускает второй exe из папки), и раньше
/// открывались два окна, два значка в трее и две независимые «оптимизации». Хуже того — второе окно
/// выглядело как «программа открылась, но пустая». Теперь второй запуск не открывает второе окно,
/// а **поднимает уже открытое**: именно этого человек и ждёт от повторного клика.
///
/// Устроено на двух именованных объектах Windows: мьютекс отвечает «я первый?», событие — «подними окно».
/// Оба живут, пока жив первый экземпляр; второй только сигналит и выходит.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CustomTweakByDoxaiz.SingleInstance.v1";
    private const string ActivateEventName = @"Local\CustomTweakByDoxaiz.Activate.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _stop = new();
    private bool _ownsMutex;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsFirst = createdNew;
        _ownsMutex = createdNew;

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    /// <summary>Первый ли это экземпляр. Второй должен позвать <see cref="SignalExisting"/> и выйти.</summary>
    public bool IsFirst { get; }

    /// <summary>Просьба поднять окно от второго экземпляра. Вызывается в фоновом потоке.</summary>
    public event Action? ActivateRequested;

    /// <summary>Второй экземпляр: попросить первый показать своё окно.</summary>
    public void SignalExisting()
    {
        try
        {
            _activate.Set();
        }
        catch (ObjectDisposedException)
        {
            // Первый уже закрылся — значит, сейчас окно и так откроется как обычно.
        }
    }

    /// <summary>Слушать просьбы «подними окно», пока программа работает.</summary>
    public void Listen()
    {
        var thread = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                // Ждём с таймаутом, чтобы поток не пережил программу.
                if (_activate.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    ActivateRequested?.Invoke();
                }
            }
        })
        {
            IsBackground = true,
            Name = "CustomTweakByDoxaiz — второй запуск",
        };

        thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Мьютекс уже ничей — значит, освобождать нечего.
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
        _activate.Dispose();
        _stop.Dispose();
    }
}
