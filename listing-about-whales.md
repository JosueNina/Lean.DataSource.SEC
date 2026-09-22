## Introduction

SEC Whales is the ownership side of the SEC's filings: who holds what, reported by the holders
themselves. The US Securities and Exchange Commission requires a large holder to say so, and the
filings that carry those disclosures are published through EDGAR as they are made. This product
collects them, one source at a time, and publishes each filing as it was filed.

It ships today with Form 13F institutional holdings. Every institutional investment manager
exercising discretion over at least 100 million dollars must file a Form 13F within 45 days of the
end of a calendar quarter, listing the covered securities it holds, from the second quarter of 2013
to the present. Further ownership filings are added to the product as they are built.

Nothing is summed, counted or averaged. The holders of a name, the shares institutions hold between
them, quarter over quarter change and concentration are all derivable from a day's records, and no
filing states any of them, so they are left to the algorithm rather than invented here. Counting
distinct filer CIKs across the records is a line of code and is what the demonstration algorithms
do.

The reporting lag is the product, not an inconvenience to be hidden. A 13F position is typically 45
to 135 days old by the time it reaches the public record, with late amendments arriving years later,
so a record is stamped with the filing date and carries the quarter it describes in `PeriodEnd`.
Measured across 11,761 filings in one window, the lag from the reported quarter end to the filing
date runs minimum 0 days, p10 16, median 42, p90 48, maximum 6,596, with 10.4 percent of filings
arriving later than the 45 day deadline. Delivering a position on the quarter end it describes would
inject every day of that gap as look-ahead, so LEAN delivers it when the dataset could first publish
it.

A record's `Time` is its filing date and its `EndTime` is midnight that night. LEAN emits a point at
its end time, so a day's filings all reach the algorithm at 00:00 the following day, after EDGAR has
finished listing that day at about 22:05 ET, so a backtest never reads a filing before it existed.
A filing whose EDGAR index came days late is added to history under its filing date.

An algorithm receives one `SEC13FHoldings` point per security per filing date, holding every
position reported for that security that day. Several managers file on the same day, and a single
manager can report the same security on more than one line when the investment discretion differs,
which the rules allow and Berkshire Hathaway does with Moody's. Those records stay apart, because
folding them together would state a number no filing contains.

## About the Provider

The [U.S. Securities and Exchange Commission](https://www.sec.gov) is the federal agency that
regulates the US securities markets. Form 13F is filed through EDGAR, the SEC's electronic filing
system, and the agency's Division of Economic and Risk Analysis republishes those filings as
structured, tab separated data sets in three month batches. The history is built from those data
sets. The daily job reads each day's filings from EDGAR itself, the information tables the data sets
are extracted from, because a batch arrives up to three months after its first filing: compared line
by line, 48 of 48 filings carry the same lines in both, and on six sample days EDGAR's daily index
lists exactly the filings the data set holds. The data is public domain, needs no account and no API
key, and the only access requirement is the descriptive User-Agent header that SEC policy asks of
all automated readers.

## Getting Started

```python
self._symbol = self.add_equity("AAPL", Resolution.DAILY).symbol
self._holdings_symbol = self.add_data(SEC13FHoldings, self._symbol).symbol
```
```csharp
_symbol = AddEquity("AAPL", Resolution.Daily).Symbol;
_holdingsSymbol = AddData<SEC13FHoldings>(_symbol).Symbol;
```

## Data Summary

The following table describes the dataset properties:

| Property | Value |
| --- | --- |
| Start Date | May 2013 |
| Asset Coverage\* | 8,520 US Equities |
| Data Density | Sparse |
| Resolution\*\* | Daily |
| Timezone | America/New_York |

\* Positions are reported by CUSIP, which is licensed and is not published, and resolve to a LEAN
`Symbol` for 97.0 percent of the reported lines, so the dataset covers most reported positions
rather than every reported name.

\*\* Positions are reported quarterly, but the managers of one quarter file across roughly fifty
different days and several quarters are live at once, so publication is close to continuous. In the
week of 3 to 7 August 2026, 1,462 filings produced 34,002 security days.

## Example Applications

SEC Whales lets you see what large holders actually hold and trade against how crowded a name is.
Examples include the following strategies:

- Screening for crowding by counting the distinct managers reporting each security, and for
  de-crowding by taking the securities whose count fell hardest against the previous quarter.
- Building an ownership change momentum signal from the quarter over quarter move in reported
  shares, and going long the names institutions are accumulating.
- Following one manager through `ManagerCik`, reading what a single fund reported quarter after
  quarter rather than what the market did in aggregate, which is what the demonstration algorithms
  do with Pershing Square.
- Screening out thinly followed names, requiring a minimum number of reporting managers before a
  security is tradeable.
- Reading the reported put and call lines alongside the share positions to see whether managers are
  hedging a name rather than simply owning it.
- Separating sole from defined discretion with `InvestmentDiscretion`, which is the difference
  between a manager's own book and the assets it merely directs.

## Meta

| Field | Value |
| --- | --- |
| name | SEC Whales |
| url | sec-whales |
| vendorName | Securities and Exchange Commission |
| website | https://www.sec.gov |
| history | May 2013 |
| reach | 8,520 US Equities |
| shortDescription | Ownership filings from the SEC as their holders filed them, starting with Form 13F institutional holdings |
| priceCTA | Free in Cloud |
| delivery | cloud only |

Tags: Financial Market Data

Licensing card:

```html
<p>Free access to SEC Whales in QuantConnect Cloud for use in backtesting or live trading.</p>
<ul>
    <li>Every position reported on Form 13F since 2013, as each manager filed it, across 8,520 US Equities</li>
    <li>Manager, reported quarter, share or principal amount, value, option side, discretion and voting authority per position</li>
    <li>Curated, clean data</li>
</ul>
```
