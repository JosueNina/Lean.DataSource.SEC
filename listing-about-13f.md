## Introduction

The SEC Form 13F Institutional Holdings dataset by the U.S. Securities and Exchange Commission
tracks how much of each US Equity institutional managers report holding, from the second quarter
of 2013 to the present. Every institutional investment manager exercising discretion over at least
100 million dollars must file a Form 13F within 45 days of the end of a calendar quarter, listing
the covered securities it holds. This dataset sums those filings per security, so one data point
answers how many managers hold a name and how much of it they hold. Data arrives the morning after
each filing becomes public, which for a quarterly filing regime means a burst of activity around each
deadline and a long tail of late filers and amendments.

The reporting lag is the product, not an inconvenience to be hidden. A position is typically 45 to
135 days old by the time it reaches the public record, with late amendments arriving years later,
so a data point is stamped with the moment it was published and carries the quarter it reports in
`PeriodEnd`. Measured across 11,761 filings in one window, the lag from the reported quarter end to
the filing date runs minimum 0 days, p10 16, median 42, p90 48, maximum 6,596, with 10.4 percent of
filings arriving later than the 45 day deadline. Delivering a position on the quarter end it
describes would inject every day of that gap as look-ahead, so LEAN delivers it when the dataset
could first publish it: EDGAR lists a day's filings at about 22:05 ET, the daily job reads them after
midnight, and every row is stamped at 03:00 ET the day after its filing date, before the market
opens. History and live carry the same stamp, so a backtest never reads a filing the live job did
not have yet.

Every value is cumulative for its `PeriodEnd`. The managers of a single quarter file across roughly
fifty different days, so a data point counts every filing for that quarter that was public by its
timestamp rather than only the ones made that day. Reading Apple on the busiest day of the March
2026 quarter gives 5,920 reporting managers and 9,097,811,804 shares, not the 602 managers who
reported it for the first time that day.

## About the Provider

The [U.S. Securities and Exchange Commission](https://www.sec.gov) is the federal agency that
regulates the US securities markets. Form 13F is filed through EDGAR, the SEC's electronic filing
system, and the agency's Division of Economic and Risk Analysis republishes those filings as
structured, tab separated data sets in three month batches. The history is built from those data
sets. The daily job reads each day's filings from EDGAR itself, the information tables the data sets
are extracted from, because a batch arrives up to three months after its first filing: compared
line by line, 48 of 48 filings carry the same lines in both, and on six sample days EDGAR's daily
index lists exactly the filings the data set holds. The data is public domain, needs no account and
no API key, and the only access requirement is the descriptive User-Agent header that SEC policy
asks of all automated readers.

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
| Asset Coverage | 6,616 US Equities |
| Data Density | Sparse |
| Resolution | Daily\* |
| Timezone | America/New_York |

\* Positions are reported quarterly, but the managers of one quarter file across roughly fifty
different days and several quarters are live at once, so publication is close to continuous rather
than quarterly. Measured on the processed files over the twelve months to May 2026, a security
carries a release on a median of 115 days (p10 29, p90 170); a widely held name such as AAPL
carries one on 234 of the roughly 250 business days.

The history begins on 2013-05-20, the first filing date in the SEC structured data set, whose first
archive covers the second quarter of 2013. Anything earlier exists only as raw filings in the EDGAR
full index and is not part of this dataset.

Each data point carries the following fields, all summed across the managers that reported the
security:

| Property | Meaning |
| --- | --- |
| `Holders` | Number of distinct managers, counted by filer CIK, that have reported the security |
| `Shares` | Shares held, summed over lines with share type SH and no option flag |
| `HoldingValue` | Market value of those same lines as reported by the managers, and the data point's `Value` |
| `CallShares` | Shares underlying reported call positions |
| `PutShares` | Shares underlying reported put positions |
| `PrincipalValue` | Principal amount of debt instruments reported for the security, share type PRN |
| `VotingSole` | Shares over which the reporting managers hold sole voting authority |
| `VotingShared` | Shares over which the reporting managers share voting authority |
| `ConfidentialOmitted` | True when at least one contributing filing withheld positions under confidential treatment |

Option lines and debt principal are deliberately kept out of `Shares`. One quarter of the source
carries 61,400 call lines, 57,443 put lines and 15,698 PRN lines, and folding any of them into a
share count would misstate the position, so they are carried as their own fields instead. For the
same reason the reported value column is only meaningful under the same filter: summed raw, with
option and PRN lines left in, it totals 70.1 trillion dollars for a single quarter.

`ConfidentialOmitted` is a point-in-time feature rather than a data quality flag. A manager can ask
the SEC to withhold specific positions temporarily, so a filing marked this way is incomplete by
design and the withheld positions appear in a later release.

### How the aggregation was validated

Every reported line is summed. Lines are not deduplicated by investment discretion and lines that
name another manager are not dropped. That looks like double counting, because 44.3 percent of
holding lines name another manager and those lines carry 54.9 percent of the reported value, so the
rule was measured rather than assumed. The three largest holdings of the 2026-03-31 period were
summed against shares outstanding, counting only share type SH with an empty option flag:

| Security | Raw sum | Sole discretion only | Lines naming no other manager |
| --- | --- | --- | --- |
| MSFT | 71.6% | 25.6% | 29.5% |
| AAPL | 63.1% | 22.6% | 26.4% |
| NVDA | 66.0% | 22.9% | 26.6% |

Published institutional ownership depends on the provider: in 2026 it runs from about 53 to 74
percent for AAPL, 54 to 70 for NVDA and 72 to 82 for MSFT. The raw sum lands inside those spreads,
or for MSFT just under them, while both of the obvious defences land far below every published
figure, because holdings reported under
defined discretion are legitimate holdings and dropping them throws away most of the institutional
base. `Holders`, being a count of distinct filer CIKs, is immune to the question either way and is
the safer headline field.

Amendments are the one place where lines are not simply summed. Most amendments restate the whole
report, and adding a restatement on top of the report it replaces counted the same position twice:
for Apple's March 2026 quarter that was 993,574,019 shares, 9.6 percent of the total. An amendment's
lines therefore count only for a security the manager had not reported for the quarter yet, which is
what an amendment adding new holdings carries. The rule only ever adds: a restatement that lowers or
removes a position leaves the original figure standing, and an amendment adding holdings to a security
the manager already reported for the quarter is not counted. Against replacing each manager's position
with its restatement, the difference on the three largest holdings of the March 2026 quarter is under
0.01 percent.

That count is taken once per manager: a manager that reports the security on several filings, under
several of the issuer's CUSIPs, or names it for the first time in an amendment counts once for the
quarter. Checked against the source tables, Apple's March 2026 quarter reads 6,041 holders where the
SEC data holds 6,040 to 6,043 distinct managers, depending on which share classes count as Apple.

### Coverage is partial by design

Holdings are keyed by CUSIP in the source and resolved to a LEAN `Symbol` before publication, so no
identifier from the source is shipped. The resolution runs in three steps, each picking up what the
one before it could not reach:

1. The CUSIP itself, looked up in LEAN's security database. On its own it accounts for 76.1 percent
   of the reported value.
2. The US ISIN, built arithmetically from the same CUSIP. It reaches issuers whose CUSIP column in
   that database is blank and lifts the two steps together to 88.9 percent of value.
3. The ticker the SEC's own Form N-PORT filings report for the CUSIP, taken back to a `Symbol`
   through the map files. This step needs no security database at all and it is what reaches the
   foreign domiciled issuers whose identifier is really a CINS, for which a constructed US ISIN is
   wrong by construction. Alphabet is one of them, which is why `GOOGL` appears in the demo
   algorithms. With this step the chain covers 98.2 percent of reported value on the most recent
   window. A fund's reported ticker is free text, so this step keeps a match only when the reported
   prices do not say otherwise. A CUSIP whose issue number carries letters, as debt does, is kept
   only when its reported prices are the security's own quarter-end close, in dollars or in
   thousands, and any other group is dropped when three or more of its prices are not. That keeps
   Etsy's convertible notes out of Etsy's share count, and Centerra Gold, Enerflex, B2Gold and DeFi
   Technologies, whose Toronto or fund-reported tickers are CG, EFX, BTO and DEFI, off Carlyle,
   Equifax, a John Hancock fund and the Hashdex DEFI ETF. A group that cannot be checked, such as a
   day of option positions only or one or two lines that are usually a single manager's slip, is
   kept.

The real limit is the third step's own history: N-PORT begins in late 2019, so a security that
stopped trading before then is not reachable through it at any depth and can only be resolved if
the first two steps already found it. This dataset therefore covers most of the reported dollars
but not every reported name, and it says so rather than implying full coverage.

Raw sums only. Quarter over quarter change, percentage of shares outstanding, concentration ratios
and new or closed position counts are all derivable from what is shipped, so they are left to the
algorithm, which is also the only place that knows the window and the ranking the strategy needs.

## Example Applications

The SEC Form 13F Institutional Holdings dataset lets you see what large managers actually hold and
trade against how crowded a name is. Examples include the following strategies:

- Screening for crowding by ranking the universe on the number of holders, and for de-crowding by
  taking the securities whose holder count fell hardest against the previous quarter.
- Building an ownership change momentum signal from the quarter over quarter move in reported
  shares, and going long the names institutions are accumulating.
- Filtering an existing universe by institutional breadth, requiring a minimum holder count before
  a security is tradeable so that thinly followed names never enter the portfolio.
- Avoiding names where institutional ownership is collapsing, using a falling holder count and a
  falling share total together as an exit condition.
- Reading the reported put and call share totals alongside the share position to see whether
  managers are hedging a name rather than simply owning it.

## Meta

| Field | Value |
| --- | --- |
| name | SEC Form 13F Institutional Holdings |
| url | sec-form-13f-institutional-holdings |
| vendorName | U.S. Securities and Exchange Commission |
| website | https://www.sec.gov |
| history | May 2013 |
| reach | 6,616 US Equities |
| shortDescription | Institutional ownership per US Equity aggregated from every Form 13F filing, published quarterly by the SEC |
| priceCTA | Free in Cloud |
| delivery | cloud only |

Tags: Financial Market Data

Licensing card:

```html
<p>Free access to SEC Form 13F Institutional Holdings in QuantConnect Cloud for use in backtesting or live trading.</p>
<ul>
    <li>Quarterly institutional ownership across 6,616 US Equities, delivered on the filing date</li>
    <li>Holder counts, share and value totals, option and debt lines, and voting authority, per security and as a universe</li>
    <li>Curated, clean data</li>
</ul>
```
