# Sample CSVs (all for samples/dev.xsd)

| File | Kind | Scenario |
|---|---|---|
| customers_clean.csv | Customers | exact-ish headers; empty optional group; quoted commas and apostrophe |
| customers_noisy.csv | Customers | spaced/hyphenated headers, extra columns (Town, Notes) that must stay unmapped |
| orders_clean.csv | Orders | hierarchy: contiguous rows with same order collapse into one Order with many Lines |
| orders_duplicate_headers.csv | Orders | two columns named `date` (ship, order): display names `date (#3)`, `date (#4)`; assign #4 to OrderDate |
| orders_abbreviated.csv | Orders | `Qty` is not suggested (abbreviation): assign manually |
| orders_bad_data.csv | Orders | row 2 quantity `many` fails XSD; row 3 missing required sku |
| products_clean.csv | Products | escaping of quotes, `&`, `<>`; empty optional boolean |
| products_blank_header.csv | Products | blank header shown as `(blank) (#3)`; never suggested |
| invoices_clean.csv | Invoices | baseline |
| invoices_reordered.csv | Invoices | same headers, different order: a mapping built from invoices_clean must fail with a layout error |
