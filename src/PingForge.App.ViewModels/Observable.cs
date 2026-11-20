using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace PingForge.App.ViewModels;

/// <summary>
/// Основа моделей представления. Своя, а не из библиотеки: две сотни строк без зависимостей —
/// это меньше, чем вес библиотеки, и видно, что именно происходит при смене свойства.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Присвоить значение и сообщить об этом интерфейсу, если оно правда изменилось.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Сообщить, что изменилось вычисляемое свойство.</summary>
    protected void OnPropertyChanged(params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>Команда без параметра. Может быть недоступна — тогда кнопка в интерфейсе гаснет.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Асинхронная команда. Защита от двойного нажатия встроена: пока работа идёт, команда недоступна.
/// Это не «мелочь интерфейса»: двойное нажатие «Оптимизировать» — это два запуска движка.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    /// <summary>
    /// Куда сообщать об исключении из команды, нажатой кнопкой. Окно при запуске подставляет
    /// <c>CrashGuard.Report</c> (журнал + отчёт + окно с текстом); в тестах обработчика нет,
    /// и тогда исключение уходит дальше — тест обязан видеть падение, а не тишину.
    /// </summary>
    public static Action<string, Exception>? OnFailure { get; set; }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>Идёт ли выполнение прямо сейчас.</summary>
    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        // async void из XAML: исключение отсюда раньше уходило в DispatcherUnhandledException,
        // то есть человек получал «ошибка в работе программы» без понимания, какая кнопка её дала.
        // Теперь оно ловится здесь, называется командой и уходит в общий отчёт об ошибке.
        try
        {
            await ExecuteAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (OnFailure is not null)
        {
            OnFailure("Ошибка при выполнении команды", ex);
        }
    }

    /// <summary>Выполнить команду и дождаться результата — так делают тесты и горячие клавиши.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
