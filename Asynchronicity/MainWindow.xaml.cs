using LiveCharts.Wpf;
using LiveCharts;
using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Asynchronicity
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
  {
    private Channel<DataItem> _channel;
    //private CancellationTokenSource _producersCts;
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

    //private ColumnSeries _producedSeries;

    public SeriesCollection SeriesCollectionConsumers { get; set; }
    public SeriesCollection SeriesCollectionProducers { get; set; }
    public List<string> LabelsProducers { get; set; }
    public List<string> LabelsConsumers { get; set; }

    public string ProducedText => $"Wyprodukowane: {_produced}";
    public string ConsumedText => $"Skonsumowane: {_consumed}";
    public string ErrorText => $"Błędy: {_errors}";
    public string AvgWaitText => $"Średni czas w kolejce: {TimeSpan.FromTicks(_consumed > 0 ? _totalWaitTicks / _consumed : 0):g}";


    public event PropertyChangedEventHandler PropertyChanged;


    public MainWindow()
    {
      InitializeComponent();
      DataContext = this;

      LabelsProducers = new List<string> ();
      LabelsConsumers = new List<string>();
      SeriesCollectionConsumers = new SeriesCollection();
      SeriesCollectionProducers = new SeriesCollection();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
      //nie określamy liczby konsumentów i producentów. Liczba konsumentów i producentów jest nieograniczona
      _channel = Channel.CreateUnbounded<DataItem>(); 
      
      _produced = _consumed = _errors = 0;
      _totalWaitTicks = 0;

      for(int i = 0; i < 2; i++)
      {
        AddProducer();
      }

      for (int i = 0; i < 3; i++)
      {
        AddConsumer();
      }

      UpdateChart();
      NotifyStats();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
      try
      {
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
      AddConsumer();
    }

    private void StopConsumer_Click(object sender, RoutedEventArgs e)
    {
      StopLastConsumer();
    }

    private void AddProducer_Click(object sender, RoutedEventArgs e)
    {
      AddProducer();
    }

    private void StopProducer_Click(object sender, RoutedEventArgs e)
    {
      StopLastProducer();
    }

    private async Task Producer(string name, CancellationToken token)
    {
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

    private void AddConsumer()
    {
      string name = $"C{_nextConsumerId++}";
      _consumerCounts[name] = 0;

      var series = new ColumnSeries
      {
        Title = name,
        Values = new ChartValues<int> { 0 }
      };

      _consumersSeries[name] = series;
      SeriesCollectionConsumers.Add(series);
      LabelsConsumers.Add(name);

      var cts = new CancellationTokenSource();
      _consumersCts[name] = cts;

      // Odśwież wykres
      OnPropertyChanged(nameof(SeriesCollection));

      Task.Run(() => Consumer(name, cts.Token));
    }

    private void AddProducer()
    {
      string name = $"P{_nextProducerId++}";
      _producerCounts[name] = 0;

      var series = new ColumnSeries
      {
        Title = name,
        Values = new ChartValues<int> { 0 }
      };

      _producersSeries[name] = series;
      SeriesCollectionProducers.Add(series);
      LabelsProducers.Add(name);

      var cts = new CancellationTokenSource();
      _producersCts[name] = cts;

      OnPropertyChanged(nameof (SeriesCollection));

      Task.Run(() => Producer(name, cts.Token));      
    }

    private void StopLastConsumer()
    {
      var lastConsumerCts = _consumersCts.LastOrDefault();
      if(lastConsumerCts.Value == null)
      {
        return;
      }
      lastConsumerCts.Value.Cancel();
      _consumersCts.Remove(lastConsumerCts.Key);
    }

    private void StopLastProducer()
    {
      var lastProducerCts = _producersCts.LastOrDefault();
      if (lastProducerCts.Value == null)
      {
        return;
      }
      lastProducerCts.Value.Cancel();
      _producersCts.Remove(lastProducerCts.Key);
    }

    private void UpdateChart()
    {
      if(Application.Current == null)
      {
        return;
      }
      Application.Current.Dispatcher.Invoke(() =>
      {
        foreach(var kvp in _producersSeries)
        {
          string name = kvp.Key;
          kvp.Value.Values[0] = _producerCounts.TryGetValue(name, out var count) ? count : 0;
        }

        foreach (var kvp in _consumersSeries)
        {
          string name = kvp.Key;
          kvp.Value.Values[0] = _consumerCounts.TryGetValue(name, out var count) ? count : 0;
        }
      });
    }

    private void NotifyStats()
    {
      Application.Current.Dispatcher.Invoke(() =>
      {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProducedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConsumedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvgWaitText)));
      });
    }

    public record DataItem(int Id, DateTime CreatedAt);

    private void OnPropertyChanged(string name)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
  }
}