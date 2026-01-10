speedtest(5) - Speedtest CLI by Ookla is the official command line client for testing the speed and performance of your internet connection.
============

## SYNOPSIS
**speedtest** [**help**] [**-aAbBfhiIpPsv**] [**--ca-certificate=path**] [**--format=**<format-type>]
[**--interface=interface**] [**--ip=ip_address**] [**--output-header**] [**--precision**=<num_decimal_places>]
[**--progress**=<yes|no>] [**--selection-details**] [**--server-id**=<id>] [**--servers**] [**--unit**=<unit-of-measure>] [**--version**]

## DESCRIPTION
**speedtest** is an application that measures the latency, jitter, packet loss, download bandwidth, and
upload bandwidth of the network connection between the client and a nearby Speedtest
Server.

## OPTIONS

* **-h, --help**:
  Print usage information

* **-v**:
  Logging verbosity, specify multiple times for higher verbosity (e.g. **-vvv**)

* **-V, --version**:
  Print version number

* **-L, --servers**:
  List nearest servers

* **--selection-details**:
  Show server selection details

* **-s** *id*, **--server-id**=<id>: 
  Specify a server from the server list using its id
  
* **-o** *hostname*, **--host**=<hostname>:
  Specify a server from the server list using its hostname

* **-f** <format_type>, **--format**=<format_type>:
  Output format (default is <human-readable>). See [OUTPUT FORMATS][] below for details.
 
* **--progress-update-interval**=<interval>:
  Progress update interval (100-1000 milliseconds)

* **--output-header**:
  Show output header for CSV and TSV formats

* **-u** <unit_of_measure>, **--unit**=<unit_of_measure>:
  Output unit for displaying speeds when using the <human-readable>
  output format. The default unit is Mbps. See [UNITS OF MEASURE][] for
  more details.

* **-a**:
  Shortcut for [**-u** <auto-decimal-bits>]
  
* **-A**:
  Shortcut for [**-u** <auto-decimal-bytes>]
  
* **-b**:
  Shortcut for [**-u** <auto-binary-bits>]
  
* **-B**:
  Shortcut for [**-u** <auto-binary-bytes>]

* **-P** <decimal_places>, **--precision**=<decimal_places>:
  Number of decimal places to use (default = 2, valid = 0-8). Only applicable to the
  <human-readable> output format.

* **-p** <yes>|<no>, **--progress**=<yes>|<no>:
  Enable or disable progress bar (default is <yes> when interactive)

* **-I** <interface>, **--interface**=<interface>:
  Attempt to bind to the specified interface when connecting to servers

* **-i** <ip_address>, **--ip**=<ip_address>:
  Attempt to bind to the specified IP address when connecting to servers

* **--ca-certificate**=<path>:
  Path to CA Certificate bundle, see [SSL CERTIFICATE LOCATIONS][] below.

## OUTPUT FORMATS

These are the available output formats for Speedtest CLI specified with the **-f** or **--format** flags. All machine readable formats 
(csv, tsv, json, jsonl, json-pretty) use bytes for data sizes, bytes per seconds for speeds and milliseconds for durations. They also always use maximum precision output.

* **human-readable**:
  human readable output
* **csv**:
  comma separated values
* **tsv**:
  tab separated values
* **json**:
  javascript object notation (compact)
* **jsonl**:
  javascript object notation (lines)
* **json-pretty**:
  javascript object notation (pretty)

## UNITS OF MEASURE

For the human-readable output format, you can specify the unit of measure to use. The default unit 
is <Mbps>. The supported units are listed below. 

These units do not apply to machine readable output formats (json, jsonl, csv and tsv).

### Decimal options (multipliers of 1000)

* **bps**:
  bits per second
* **kbps**:
  kilobits per second
* **Mbps**:
  megabits per second
* **Gbps**:
  gigabits per second
* **B/s**:
  bytes per second
* **kB/s**:
  kilobytes per second
* **MB/s**:
 megabytes per second
* **GB/s**:
  gigabytes per second
 
### Binary options (multipliers of 1024)
* **kibps**:
  kibibits per second
* **Mibps**:
  mebibits per second
* **Gibps**:
  gibibits per second
* **kiB/s**:
  kibibytes per second
* **MiB/s**:
  mebibytes per second
* **GiB/s**:
  gibibytes per second

### Auto-scaling options
Automatic units will scale the prefix depending on the measured speed. 

* **auto-decimal-bits**:
  automatic in decimal bits
* **auto-decimal-bytes**:
  automatic in decimal bytes
* **auto-binary-bits**:
  automatic in binary bits
* **auto-binary-bytes**:
  automatic in binary bytes
 
## TERMS OF USE AND PRIVACY POLICY NOTICES
You may only use this Speedtest software and information generated from it for personal, non-commercial use,
through a command line interface on a personal computer.  Your use of this software is subject to the End User
License Agreement, Terms of Use and Privacy Policy at these URLs:

* [https://www.speedtest.net/about/eula](https://www.speedtest.net/about/eula)
* [https://www.speedtest.net/about/terms](https://www.speedtest.net/about/terms)
* [https://www.speedtest.net/about/privacy](https://www.speedtest.net/about/privacy)

## OUTPUT
Upon successful execution, the application will exit with an exit code of 0. The result will include
latency, jitter, download, upload, packet loss (where available), and a result URL.

Latency and jitter will be represented in milliseconds. Download and upload units will depend on the output
format as well as if a unit was specified. The human-readable format defaults to Mbps and any machine-readable
formats (csv, tsv, json, jsonl, json-pretty) use bytes as the unit of measure with max precision. Packet loss is represented as a percentage, or **Not available** when packet loss is unavailable in the executing network environment.

The bytes per second measurements can be transformed into the human-readable output format
default unit of megabits (Mbps) by dividing the bytes per second value by 125,000.  For example:

38404104 bytes per second = 38404104 / 125 = 307232.832 kilobits per second = 307232.832 / 1000 = 307.232832 megabits per second

The value 125 is derived from 1000 / 8 as follows:

1 byte = 8 bits
1 kilobit = 1000 bits

38404104 bytes per second = 38404104 * 8 bits per byte = 307232832 bits per second = 307232832 / 1000 bits per kilobit = 307232.832 kilobits per second

The Result URL is available to share your result, appending **.png** to the Result URL will create a
shareable result image.

*Example human-readable result:*

```
$ speedtest
   Speedtest by Ookla

      Server: SUNET - Stockholm (id: 26852)
         ISP: Bahnhof AB
Idle Latency:     5.04 ms   (jitter: 0.04ms, low: 5.01ms, high: 5.07ms)
    Download:   968.73 Mbps (data used: 117.5 MB)                                                   
                 12.10 ms   (jitter: 1.71ms, low: 6.71ms, high: 18.82ms)
      Upload:   942.13 Mbps (data used: 114.8 MB)                                                   
                  9.94 ms   (jitter: 1.10ms, low: 5.30ms, high: 12.72ms)
 Packet Loss:     0.0%
  Result URL: https://www.speedtest.net/result/c/d1c46724-50a3-4a59-87ca-ffc09ea014b2
```

## NETWORK TIMEOUT VALUES
By default, network requests set a timeout of **10** seconds. The only exception to this
is latency testing, which sets a timeout of **15** seconds.

## FATAL ERRORS
Upon fatal errors, the application will exit with a non-zero exit code.

**Initialization Fatal Error Examples:**

*Configuration - Couldn't connect to server (Network is unreachable)*

*Configuration - Could not retrieve or read configuration (ConfigurationError)*

**Stage Execution Fatal Error Example:**

*[error] Error: [1] Latency test failed for HTTP*

*[error] Error: [36] Cannot open socket: Operation now in progress*

*[error] Failed to resolve host name. Cancelling test suite.*

*[error] Host resolve failed: Exec format error*

*[error] Cannot open socket: No route to host*

*[error] Server Selection - Failed to find a working test server. (NoServers)*

## SSL CERTIFICATE LOCATIONS
By default the following paths are checked for CA certificate bundles on linux machines:

    /etc/ssl/certs/ca-certificates.crt
    /etc/pki/tls/certs/ca-bundle.crt
    /usr/share/ssl/certs/ca-bundle.crt
    /usr/local/share/certs/ca-root-nss.crt
    /etc/ssl/cert.pem

If the device under test does *not* have one of the above mentioned files, then the canonical and up to date CA certificate bundle provided by the curl project can be manually
downloaded into a specific location.  This specific location can be provided as a parameter per the following example:

    wget https://curl.se/ca/cacert.pem
    ./ookla --ca-certificate=./cacert.pem

## RELEASE NOTES

### 1.2.0 (2022-07-27)
* Cleaned up formatting in human-readable output for additional data within parenthesis (now using `label: value` consistently)
* Compressed result upload data to reduce data usage
* Added support for measuring responsiveness (latency during load)
* Added experimental support for multi-server testing
* Updated third-party dependencies: cURL 7.83.1, mbed TLS 3.1.0, Boost 1.79.0
* Added stability improvements

### 1.1.1 (2021-11-15)
* Fixed issue with reported client version in uploaded results

### 1.1.0 (2021-10-27)
* Use server-side upload measurements
* Performance enhancement on upload tests for CPU constrained devices
* Security enhancements
* Fix for deadlock bug
* Fix crash due to race condition
* Fix crash in hostname resolution during test initialization
* Fix potential buffer overflow
* Update Boost to 1.77.0
* Update mbed TLS to 2.27.0
* Update cURL to 7.78.0

### 1.0.0 (2019-10-29)
* Initial release

## COPYRIGHT NOTICES FOR THIRD-PARTY PRODUCTS/LIBRARIES
This software incorporates free and open source third-party libraries, including:

* [boost](https://www.boost.org/)
* [libcurl](https://curl.haxx.se/libcurl/)
* [petopt](https://www.lysator.liu.se/~pen/petopt/)
* [mbed TLS](https://tls.mbed.org/)
* [ca-certificates extract](https://curl.haxx.se/docs/caextract.html)
* [L. Peter Deutsch’s md5](https://sourceforge.net/projects/libmd5-rfc/files/)
* [getopt.h](in Windows version of this software)
* [tiny-aes](https://github.com/kokke/tiny-AES-c)
* [PicoSHA2](https://github.com/okdshin/PicoSHA2)
* [musl](https://www.musl-libc.org/)

Inclusion of mbed TLS is subject to presentation of the following license terms
to recipients of this software: [http://www.apache.org/licenses/LICENSE-2.0](http://www.apache.org/licenses/LICENSE-2.0)
(a copy of which is included with the documentation of this software)

### Inclusion of libcurl is subject to distribution of the software with the following notice:

    Copyright (c) 1996 - 2019, Daniel Stenberg, daniel@haxx.se, and many contributors,
    see the THANKS file.  All rights reserved.  Permission to use, copy, modify, and distribute
    this software for any purpose with or without fee is hereby granted, provided that
    the above copyright notice and this permission notice appear in all copies.

### Inclusion of getopt.h is subject to distribution of the software with the following notice:

    DISCLAIMER
    This file is part of the mingw-w64 runtime package.

    The mingw-w64 runtime package and its code is distributed in the hope that it
    will be useful but WITHOUT ANY WARRANTY.  ALL WARRANTIES, EXPRESSED OR
    IMPLIED ARE HEREBY DISCLAIMED.  This includes but is not limited to
    warranties of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.


    Copyright (c) 2002 Todd C. Miller <Todd.Miller@courtesan.com>

    Permission to use, copy, modify, and distribute this software for any
    purpose with or without fee is hereby granted, provided that the above
    copyright notice and this permission notice appear in all copies.

    Copyright (c) 2000 The NetBSD Foundation, Inc.
    All rights reserved.

    This code is derived from software contributed to The NetBSD Foundation
    by Dieter Baron and Thomas Klausner.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions
    are met:
    1. Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer.
    2. Redistributions in binary form must reproduce the above copyright
       notice, this list of conditions and the following disclaimer in the
       documentation and/or other materials provided with the distribution.

### Inclusion of PicoSHA2 is subject to distribution of the software with the following notice:

    Copyright (c) 2017 okdshin

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

### Inclusion of musl is subject to distribution of the software with the following notice:

    Copyright © 2005-2019 Rich Felker, et al.

    Permission is hereby granted, free of charge, to any person obtaining
    a copy of this software and associated documentation files (the
    "Software"), to deal in the Software without restriction, including
    without limitation the rights to use, copy, modify, merge, publish,
    distribute, sublicense, and/or sell copies of the Software, and to
    permit persons to whom the Software is furnished to do so, subject to
    the following conditions:

    The above copyright notice and this permission notice shall be
    included in all copies or substantial portions of the Software.
