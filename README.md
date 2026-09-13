# QMC - Qualcomm MDM9K Control Center

A Windows application built with WinUI 3 and .NET 8 to monitor and control Qualcomm 4G modem status, manage Wi-Fi, RAS connectivity, and SMS through a serial (COM) interface using AT commands.

This application does not communicate directly with the cellular network. All operations, such as reading and sending SMS, detecting operators, checking network mode, and managing Wi-Fi, are handled by sending AT commands to the modem firmware and parsing the responses.

---

## Modem Architecture and Communication

**Basic communication flow:**  
`ModemController (WinUI 3) → Serial Port (COM) → Qualcomm Modem Firmware → Cellular Network`

The application automatically scans and detects available COM ports, testing each one using the `AT` command. Any port that responds with `OK` is identified as an active modem port.

>**Hardware Note:**  
Most modems provide several virtual port channels (such as DM Service, Voice Device, and AT Port). This application communicates specifically on a valid AT Port, while voice/telephony ports are ignored as most commercial hardware focuses on data and SMS.

### Serial Command Gate

Because the modem uses a single serial communication channel, the application implements a serial gate (with a semaphore) to prevent concurrent AT command transmissions from different modules (Dashboard, SMS Polling, Settings). This avoids data collisions and response mix-ups.

---

## Dashboard and Diagnostics

The dashboard is the central control point for real-time hardware status and network connectivity.

### Modem Identification

The application retrieves hardware and firmware details using Qualcomm commands such as:  
`ATI`, `AT+CGMI`, `AT+CGMM`, `AT+CGMR`, and `AT^HWVER`.  
Relevant information includes manufacturer, model, firmware version, and hardware revision. The application normalizes this data to omit generic error information (such as non-informative model numbers).

### Operator and Network Technology

Operator and Radio Access Technology details are obtained with the `AT+COPS?` command.  
- Example modem response: `+COPS: 0,0,"XL",7` (where `7` indicates LTE/E-UTRAN).
- The application reformats this as: `4G LTE - XL`.
- For regular polling, the command `AT+COPS=?` (manual network search) is avoided to prevent latency or potential interference with an active connection.

### Wi-Fi Control

The modem enables wireless access point status and control via `AT+WIFI?` (read status) and `AT+WIFI=0`/`AT+WIFI=1` (off/on).  
Response `+WIFI: 0` is interpreted as OFF, while `+WIFI: 1` means ON.

---

## SMS Management and SQLite

The application explicitly locks SMS storage to the SIM card with `AT+CPMS="SM","SM","SM"`. There is a clear division of message storage:
1. **SIM Storage:** Physical SMS data from the operator, stored on the SIM.
2. **SQLite Local Database:** Local conversational history, stored at `%LOCALAPPDATA%\Qualcomm MDM9K 4G Control Center\sms.db`.

### SMS Deletion Protection and Parsing

To avoid message loss due to data structure parsing failures, messages are deleted from SIM memory (using `AT+CMGD`) **only after** being successfully read and fully processed into local storage.

### Encoding and Phone Number Normalization

- **Text Decoding:** Supports decoding of GSM and UCS2/UTF-16BE hexadecimal text for proper display of complex or multilingual messages.
- **Phone Number Normalization:** Indonesian phone numbers are standardized to international format (e.g., `0812...`, `6281...`, `+6281...` all normalized to `+6281...`) for conversation grouping. Operator short codes (such as `363`, `444`) are excluded so that their services remain functional.

### Duplicate SMS Sending Protection

SMS sending via `AT+CMGS` is protected during network submission. If the response is interrupted after submitting the message to the network, the application prevents auto-retry to avoid duplicate message delivery.

---

## Local Contacts System

Contacts are managed independently in a local SQLite database, separate from the modem's SIM card memory. Key features include:
- Storage uses the `NormalizedAddress` column as a unique key to prevent duplicate contacts.
- Automatic contact lookup in the inbox and chat pages to display contact names instead of raw numbers.
- Direct shortcuts from the chat page to the Contacts menu for editing or adding new numbers.

---

## Technical Notes on CUSD (USSD)

Testing with `AT+CUSD` shows that USSD features (such as checking balance with `*123#`) return `+CME ERROR: unknown` on certain modems.

This outcome suggests that USSD support depends on a complex compatibility chain:  
`Application → Modem Firmware → USSD Stack → Mobile Network → Operator USSD Service`

The failure does **not** mean AT USSD commands are inherently broken, but is due to specific limitations in older (Cat.4) Qualcomm modem firmware or operator policies that block USSD on data-only modems. Therefore, USSD is not included as a main application feature. SMS-based operator short codes continue to work normally.

---

## Functional Architecture Summary

**QMC - Qualcomm 4G Control Center: Main Features**

- Dashboard: modem detection, hardware info, operator and network status, Wi-Fi control
- SMS: SIM management, polling, smart parsing, duplicate elimination, local history
- Contacts: local address book, name matching, and number normalization
- Serial Communication: automatic port scanning, synchronized access

**Overall workflow:**  
`QMC Application → Serial Port (COM) → Qualcomm Modem → Cellular Network/SIM`