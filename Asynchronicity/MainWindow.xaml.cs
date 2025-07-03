using LiveCharts.Wpf;
using LiveCharts;
using System.ComponentModel;
using System.Threading.Channels;
using System.Windows;
using System.Collections.ObjectModel;
//using System.Windows.Controls;


namespace Asynchronicity
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
  {
    private Channel<DataItem> _channel;
    private readonly object _lock = new();

    private Dictionary<string, int> _consumerCounts = new();
    private Dictionary<string, int> _producerCounts = new();
    private Dictionary<string, ColumnSeries> _consumersSeries = new();
    private Dictionary<string, ColumnSeries> _producersSeries = new();
    private Dictionary<string, CancellationTokenSource> _consumersCts = new();
    private Dictionary<string, CancellationTokenSource> _producersCts = new();

    private int _nextProducerId = 1;
    private int _nextConsumerId = 1;

    private int _produced, _consumed, _errors;
    private long _totalWaitTicks;

    public SeriesCollection SeriesCollectionConsumers { get; set; }
    public SeriesCollection SeriesCollectionProducers { get; set; }
    public ObservableCollection<string> LabelsProducers { get; set; }
    public ObservableCollection<string> LabelsConsumers { get; set; }

    public string ProducedText => $"Wyprodukowane: {_produced}";
    public string ConsumedText => $"Skonsumowane: {_consumed}";
    public string ErrorText => $"Błędy: {_errors}";
    public string AvgWaitText => $"Średni czas w kolejce: {TimeSpan.FromTicks(_consumed > 0 ? _totalWaitTicks / _consumed : 0):g}";


    public event PropertyChangedEventHandler PropertyChanged;


    public MainWindow()
    {
      InitializeComponent();
      DataContext = this;

      LabelsProducers = new ObservableCollection<string>();
      LabelsConsumers = new ObservableCollection<string>();
      SeriesCollectionConsumers = new SeriesCollection();
      SeriesCollectionProducers = new SeriesCollection();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
      _channel = Channel.CreateUnbounded<DataItem>(); 
      _produced = _consumed = _errors = 0;
      _totalWaitTicks = 0;

      for(int i = 0; i < 2; i++)
      {

        AddWorker(WorkerType.Producer, ref _nextProducerId, _producerCounts, _producersSeries,
                  SeriesCollectionProducers, LabelsProducers, _producersCts, Producer); 
      }

      for (int i = 0; i < 3; i++)
      {
        AddWorker(WorkerType.Consumer, ref _nextConsumerId, _consumerCounts, _consumersSeries,
          SeriesCollectionConsumers, LabelsConsumers, _consumersCts, Consumer);
      }

      UpdateChart();
      NotifyStats();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
      try
      {
        if(_channel == null)
        {
          return;
        }
        _channel.Writer.Complete();
        foreach(var token in _producersCts)
        {
          token.Value.Cancel();
        }
        foreach(var token in _consumersCts)
        {
          token.Value.Cancel();
        }
      }
      catch (Exception) { }
    }
    private void AddConsumer_Click(object sender, RoutedEventArgs e)
    {
      AddWorker(WorkerType.Consumer, ref _nextConsumerId, _consumerCounts, _consumersSeries,
          SeriesCollectionConsumers, LabelsConsumers, _consumersCts, Consumer);
    }

    private void StopConsumer_Click(object sender, RoutedEventArgs e)
    {
      StopLastWorker(_consumersCts);
    }

    private void AddProducer_Click(object sender, RoutedEventArgs e)
    {

      AddWorker(WorkerType.Producer, ref _nextProducerId, _producerCounts, _producersSeries,
                SeriesCollectionProducers, LabelsProducers, _producersCts, Producer);
    }

    private void StopProducer_Click(object sender, RoutedEventArgs e)
    {
      StopLastWorker(_producersCts);
    }

    private async Task Producer(string name, CancellationToken token)
    {
      if(_channel == null)
      {
        return;
      }
      var rnd = new Random();
      int id = 0;
      try
      {
        while (!token.IsCancellationRequested)
        {
          var item = new DataItem(id++, DateTime.UtcNow);
          await _channel.Writer.WriteAsync(item, token);

          lock (_lock)
          {
            _produced++;
            if (_producerCounts.ContainsKey(name))
              _producerCounts[name]++;
            UpdateChart();
            NotifyStats();
          }

          await Task.Delay(200 + rnd.Next(0, 400), token);
        }
      }
      catch (OperationCanceledException) { }
    }

    private async Task Consumer(string name, CancellationToken token)
    {
      if (_channel == null)
      {
        return;
      }
      var rnd = new Random();
      try
      {
        await foreach (var item in _channel.Reader.ReadAllAsync(token))
        {
          var wait = DateTime.UtcNow - item.CreatedAt;
          //symulacja wykonania zadania
          await Task.Delay(500 + rnd.Next(0, 300), token);

          lock (_lock)
          {
            _consumed++;
            _totalWaitTicks += wait.Ticks;

            //symulacja błędu
            if (rnd.NextDouble() < 0.1)
              _errors++;

            if (_consumerCounts.ContainsKey(name))
              _consumerCounts[name]++;

            UpdateChart();
            NotifyStats();
          }
        }
      }
      catch (OperationCanceledException) { }
    }

    private void AddWorker(
     WorkerType workerType,
     ref int nextId,
     Dictionary<string, int> countDict,
     Dictionary<string, ColumnSeries> seriesDict,
     SeriesCollection seriesCollection,
     ObservableCollection<string> labels,
     Dictionary<string, CancellationTokenSource> ctsDict,
     Func<string, CancellationToken, Task> workerFunc)
    {
      string name = $"{GetWorkerPrefix(workerType)}{nextId++}";
      countDict[name] = 0;

      var series = new ColumnSeries
      {
        Title = name,
        Values = new ChartValues<int> { 0 }
      };

      seriesDict[name] = series;
      seriesCollection.Add(series);
      labels.Add(name);

      var cts = new CancellationTokenSource();
      ctsDict[name] = cts;

      Task.Run(() => workerFunc(name, cts.Token));

    }

    private string GetWorkerPrefix(WorkerType workerType)
    {
      return workerType switch
      {
        WorkerType.Consumer => "C",
        WorkerType.Producer => "P",
        _ => throw new Exception($"WorkerType {workerType} not found!")
      };        
    }

    private void StopLastWorker(Dictionary<string, CancellationTokenSource> ctsDict)
    {
      var last = ctsDict.LastOrDefault();
      if (last.Value == null)
        return;

      last.Value.Cancel();
      ctsDict.Remove(last.Key);
    }

    private void UpdateChart()
    {
      if(Application.Current == null)
      {
        return;
      }
      Application.Current.Dispatcher.Invoke(() =>
      {
        UpdateSeries(_producersSeries, _producerCounts);
        UpdateSeries(_consumersSeries, _consumerCounts);
      });
    }

    private void UpdateSeries(Dictionary<string, ColumnSeries> seriesDict, Dictionary<string, int> countDict)
    {
      foreach (var kvp in seriesDict)
      {
        string name = kvp.Key;
        kvp.Value.Values[0] = countDict.TryGetValue(name, out var count) ? count : 0;
      }
    }

    private void NotifyStats()
    {
      if(Application.Current == null)
      {
        return;
      }
      Application.Current.Dispatcher.Invoke(() =>
      {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProducedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConsumedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvgWaitText)));
      });
    }

    public record DataItem(int Id, DateTime CreatedAt);

  }
}