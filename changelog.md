### 0.9.9.4 (2016-04-27)
* new: support synthetic order (order trigger order) 

### 0.9.9.3 (2016-04-13)
* new: support CQG order submission
* new: display the version check button in the ribbon
* new: use the 'tag' for the 'submittedby' in the order grouping
* fix: counter symbol should be case insensitive for non-CTP channels
* fix: registration & password reset server list highlight issue
* fix: order group statistic double count replace order qty 

### 0.9.9.2 (2016-04-05)
### 0.9.9.1 (2016-04-04)
* fix: CTP price subscription issue 

### 0.9.9 (2016-03-29)
* new: change to name Xceder
* new: account registration and password reset
* new: CQG/Orient support
* new: voice function to be used in the Excel
* new: counter price stale alert


### 0.9.8 (2016-02-24)

* new: support CTP price & order
* new: disable the order submission & retainer when logout
* new: add voice & sound prompt on order status update
* new: clear order & price records after logout
* new: add new button to withdraw all queueing orders
* new: add real time system status and feature on/off update
* new: detect the new version before connect server. So if the message protocol is changed, still be able to upgrade
* new: use github as the file depository
* new: open VC++ library download page if not installed 

* fix: clear the cached provider status after login 

### 0.9.7 (2016-02-24)

* new: provide the price field array
* new: provide the on/off button to enable/disable order submission & retainers
* new: provide the TT price lader in the demo worksheet
* new: provide the real time status of the provider
* fix: price update stale

### 0.9.6 (2016-02-17)

* show disconnected provider icon when network is broken

### 0.9.5 (2016-02-05)

* remove the "side" parameter from _trade() & _orderRetainer(). use the +/- qty indicate the side, buy/sell
* fix the order list sorting bug in the order group

### 0.9.4 (2016-02-04)

* display the OrderID from the provider 
* support the duplicate ExecID report for Phillip & Square 
* add the order create time period filter for the order status/statistic
* simplify the _order()/_orderStatistic() parameter
* only monitor the submitted order by _trade() itself

### 0.9.3 (2016-01-31)

* provide the order group statistic formula function for real time info
* separate the functions into the different worksheet in the readme file
* demo how to use the order group statistic to implement the hedging

### 0.9.2 (2016-01-28)

* support dead connection check through the provider status query
* fix the Excel auto-load registration issue
* reduce the dead connection check period from 5 min to 1 min


### 0.9.1 (2016-01-26)

* support server side broadcasting notice message
* support the same user ID login kicked out message
* support the multiple server address list in the ribbon
