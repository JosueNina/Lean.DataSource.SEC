## Requesting Data

To add SEC Form 13F Institutional Holdings data to your algorithm, call the **AddData** method with
the `Symbol` of an Equity you already subscribe to. Save a reference to the dataset **Symbol** so you
can access the data later in your algorithm.

```python
class SEC13FHoldingsDataAlgorithm(QCAlgorithm):

    def initialize(self) -> None:
        # Coverage runs 2013-05-20 to 2026-05-29
        self.set_start_date(2020, 10, 1)
        self.set_end_date(2020, 12, 31)
        self.set_cash(100000)

        self._symbol = self.add_equity("AAPL", Resolution.DAILY).symbol
        self._dataset_symbol = self.add_data(SEC13FHoldings, self._symbol).symbol
```
```csharp
public class SEC13FHoldingsDataAlgorithm : QCAlgorithm
{
    private Symbol _symbol, _datasetSymbol;

    public override void Initialize()
    {
        // Coverage runs 2013-05-20 to 2026-05-29
        SetStartDate(2020, 10, 1);
        SetEndDate(2020, 12, 31);
        SetCash(100000);

        _symbol = AddEquity("AAPL", Resolution.Daily).Symbol;
        _datasetSymbol = AddData<SEC13FHoldings>(_symbol).Symbol;
    }
}
```

## Accessing Data

To get the current SEC Form 13F Institutional Holdings data, index the current [Slice](https://www.quantconnect.com/docs/v2/writing-algorithms/key-concepts/time-modeling/timeslices)
with the dataset **Symbol**. **Slice** objects deliver unique events to your algorithm as they
happen, but the **Slice** may not contain data for your dataset at every time step. Positions are
reported quarterly, but the managers of one quarter file across roughly fifty different days, so
publication is close to continuous: measured on the processed files a security carries a release on
a median of 111 days a year, and a widely held name on 234 of the roughly 250 business days. Check
that the **Slice** contains the data you want before you index it.

```python
def on_data(self, slice: Slice) -> None:
    if self._dataset_symbol in slice:
        data_point = slice[self._dataset_symbol]
        holders = data_point.holders
        shares = data_point.shares
```
```csharp
public override void OnData(Slice slice)
{
    if (slice.ContainsKey(_datasetSymbol))
    {
        var dataPoint = slice[_datasetSymbol];
        var holders = dataPoint.Holders;
        var shares = dataPoint.Shares;
    }
}
```

To iterate through all of the dataset objects in the current **Slice**, call the **Get** method. A
filing deadline puts thousands of securities into the same day, so this is the usual shape when you
subscribe to more than one name.

```python
def on_data(self, slice: Slice) -> None:
    for dataset_symbol, data_point in slice.get(SEC13FHoldings).items():
        holders = data_point.holders
```
```csharp
public override void OnData(Slice slice)
{
    foreach (var kvp in slice.Get<SEC13FHoldings>())
    {
        var datasetSymbol = kvp.Key;
        var dataPoint = kvp.Value;
        var holders = dataPoint.Holders;
    }
}
```

A data point's `Time` and `EndTime` are both the moment the filing became public, and the quarter
the numbers describe is carried in `PeriodEnd`. There is no publication offset to model: the point
IS the publication. Your algorithm therefore reads a position only once the public record held it.
The gap between the two is not a constant and cannot be derived: measured across 11,761 filings in
one window it runs minimum 0 days, p10 16, median 42, p90 48, maximum 6,596, and 10.4 percent of
filings arrive later than the 45 day deadline. A point that arrives today therefore describes a
quarter that ended typically 45 to 135 days ago, with late amendments arriving years later, and a
single day of filings carries several different reported quarters. Read `PeriodEnd` whenever you
need the quarter a point describes rather than the day it arrived.

That last point has a consequence worth knowing before you build on it. A filing date often carries
several reported quarters for the same security, and indexing the **Slice** by **Symbol** returns
one data point per timestamp: the highest `PeriodEnd`, which during a quarter handover is the thin
new quarter rather than the finished one. Iterate `slice.AllData` (`slice.all_data` in Python) to
see every quarter reported that day, and read `PeriodEnd` before comparing `Holders` across points.
**History** returns all of them too, and the universe file ships both live quarters as separate
records on purpose.

Every value is cumulative for its `PeriodEnd`, counting every filing for that quarter that was
public by the point's timestamp. You do not have to accumulate anything yourself, and you should
not: adding two points of the same quarter together would double count.

```python
def on_data(self, slice: Slice) -> None:
    if self._dataset_symbol in slice:
        data_point = slice[self._dataset_symbol]
        self.log(f"Reported for the quarter ending {data_point.period_end}, filed {data_point.end_time}")
```
```csharp
public override void OnData(Slice slice)
{
    if (slice.ContainsKey(_datasetSymbol))
    {
        var dataPoint = slice[_datasetSymbol];
        Log($"Reported for the quarter ending {dataPoint.PeriodEnd}, filed {dataPoint.EndTime}");
    }
}
```

Every numeric field is declared nullable, but every shipped row populates all of them: a quarter
that carries share lines and no option lines reports zero for the option totals rather than leaving
them empty. Zero means the managers reported nothing of that kind, which is a reading and not a gap.
The nullability is there so the type can express a gap if the source ever produces one, so the
defensive check costs nothing, but do not expect `null` in the published history.

```python
holders = data_point.holders or 0
shares_per_holder = data_point.shares / holders if holders else 0
```
```csharp
var holders = dataPoint.Holders ?? 0m;
var sharesPerHolder = holders == 0m ? 0m : dataPoint.Shares.GetValueOrDefault() / holders;
```

`ConfidentialOmitted` is true when at least one of the filings behind the point withheld positions
under confidential treatment. Such a filing is incomplete by design and the withheld positions
surface in a later release, so treat a flagged point as a floor on the reported position rather than
as the full picture.

## Historical Data

To get historical SEC Form 13F Institutional Holdings data, call the **History** method with the
dataset **Symbol**. If there is no data in the period you request, the history result is empty.

```python
# DataFrame
history_df = self.history(self._dataset_symbol, timedelta(days=730), Resolution.DAILY)

# Dataset objects
history_bars = self.history[SEC13FHoldings](self._dataset_symbol, timedelta(days=730), Resolution.DAILY)
```
```csharp
var history = History<SEC13FHoldings>(_datasetSymbol, TimeSpan.FromDays(730), Resolution.Daily);
```

Ask for a time span rather than a bar count. A bar count is read in daily bars and the dataset
publishes on filing dates only, so what comes back depends on how widely the security is held
rather than on the count you asked for. Measured on the processed files, AAPL carries a release on
96 of the last 100 business days and on 235 of the last 365 calendar days, while a thinly held name
carries one on closer to 40 days a year. A time span is the safer form whenever you need a known
number of quarters of history.

For more information about historical data, see [History Requests](https://www.quantconnect.com/docs/v2/writing-algorithms/historical-data/history-requests).

## Universe Selection

To select a dynamic universe of US Equities based on SEC Form 13F Institutional Holdings data, call
the **AddUniverse** method with the **SEC13FHoldingsUniverse** class and a selection function. The
universe file is keyed by release date and a single release date carries filings for several
reported quarters, so read `PeriodEnd` on a record when the quarter matters to your selection.

```python
def initialize(self) -> None:
    self.universe_settings.resolution = Resolution.DAILY
    self.add_universe(SEC13FHoldingsUniverse, self._select)

def _select(self, data: List[SEC13FHoldingsUniverse]) -> List[Symbol]:
    # Select the most widely held securities of the release
    selected = sorted([datum for datum in data if datum.holders is not None],
                      key=lambda datum: (-datum.holders, datum.symbol.value))[:20]
    return [datum.symbol for datum in selected]
```
```csharp
public override void Initialize()
{
    UniverseSettings.Resolution = Resolution.Daily;

    AddUniverse<SEC13FHoldingsUniverse>(data =>
    {
        // Select the most widely held securities of the release
        return (from SEC13FHoldingsUniverse datum in data
                where datum.Holders.HasValue
                orderby datum.Holders.Value descending, datum.Symbol.Value
                select datum.Symbol).Take(20);
    });
}
```

For more information about dynamic universes, see [Universes](https://www.quantconnect.com/docs/v2/writing-algorithms/universes/key-concepts).

## Remove Subscriptions

To remove your subscription to SEC Form 13F Institutional Holdings data, call the **RemoveSecurity**
method.

```python
self.remove_security(self._dataset_symbol)
```
```csharp
RemoveSecurity(_datasetSymbol);
```

If you subscribe to SEC Form 13F Institutional Holdings data for assets in a dynamic universe,
remove the dataset subscription when the asset leaves your universe. To view a common design
pattern, see [Track Security Changes](https://www.quantconnect.com/docs/v2/writing-algorithms/algorithm-framework/alpha/key-concepts#05-Track-Security-Changes).
