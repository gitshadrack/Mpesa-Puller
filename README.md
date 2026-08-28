# MPesa Message Puller

`MPesa.exe` reads unread SMS messages from a GSM modem and imports them into SQL Server.

## Source Layout

- `Program.cs`: application startup and dependency construction.
- `MainForm.cs`: Windows UI, tray menu, polling lifecycle, and operator actions.
- `GsmModem.cs`: serial-port communication, SIM PIN handling, network diagnostics, and SMS retrieval.
- `SmsParser.cs`: M-Pesa filtering, field parsing, and network-name mapping.
- `DatabaseWriter.cs`: SQL settings, retries, duplicate checks, and transaction inserts.
- `Models.cs`: configuration and data records shared by the components.

## Setup

1. Run `MPESAscript.sql` against the target SQL Server. The script creates the `Restaurant` database objects.
2. Add or update the single row in `dbo.txGSMSettings`. `PortNo` selects the modem port and `CheckMessages` controls whether polling is enabled.
3. Edit `publish/appsettings.json`:
   - `ConnectionStrings:Restaurant`: SQL Server connection string.
   - `MpesaPuller:PortName`: fallback modem COM port if `txGSMSettings.PortNo` is empty.
   - `MpesaPuller:BaudRate`: modem baud rate, commonly `9600` or `115200`.
4. Connect the modem, insert the M-Pesa SIM, and confirm it appears in Windows Device Manager.
5. Run `publish/MPesa.exe`.

The puller checks for unread messages every second by default. Each M-Pesa message is inserted immediately in its own database transaction before the next message is processed. Non-M-Pesa messages are never sent to SQL Server and are deleted from the modem when `DiscardNonMpesaMessages` is true. Set it to false if other SMS messages must be retained on the SIM. It first probes the `PortNo` stored in `dbo.txGSMSettings`; if that port is unavailable or does not answer `AT`, it probes all available COM ports. When a modem answers, its port number is saved automatically to `txGSMSettings.PortNo` and used immediately. This means the modem may be inserted after the app starts without manual port entry. It stores the complete original text in `dbo.txMessagesRaw`, stores one fitted transaction row in `dbo.txMessages`, and deletes an imported SMS from the modem only after a successful database commit. If the text exceeds `txMessages.RawMessage`'s 3000-character limit, that column receives the first 3000 characters and the complete message remains in `txMessagesRaw`. Unparseable M-Pesa messages are retained with status `Review` for manual handling.

The parser also extracts M-Pesa transaction amounts, charges/fees, mobile numbers, sender names, and bill/account/reference values when those fields appear in the SMS. At modem startup the app reads `txGSMSettings.PIN`, checks `AT+CPIN?`, and submits the PIN only when the modem reports `SIM PIN`; already-ready SIMs are left alone. The modem test also checks `AT+CCID` and displays `SIM detected` or `SIM not detected`. The PIN is never shown in the UI or logs. The window shows SQL connection status and modem COM port, signal strength, and network. SQL connection opening retries up to three times before reporting an error.

SMS reading accepts common GSM `+CMGL` variations, including empty sender-name fields and timezone suffixes in timestamps.

Duplicate protection checks both the complete SMS text and the parsed transaction number. The first occurrence is stored; later copies are not inserted and are removed from the modem when `DeleteAfterImport` is enabled.

Masked phone numbers such as `0715***897` are stored in `txMessages.MobileNo`; the sender name is stored separately in `txMessages.SenderName`.

Each GSM settings row represents the machine's cashier profile. `MasterTill` identifies the till assigned to that modem, and `Entity` identifies the business/cashier context. When `PrintReceipt` is enabled, `PrinterName` must match an installed Windows printer; `Copies` controls the number of receipts. `PrinterDriver` and `PrinterPort` are retained as profile information, while Windows selects the actual printer by `PrinterName`. A payment is saved to SQL before printing is attempted; a printer failure is shown without losing the saved payment.

## Build

From this folder:

```text
dotnet publish MPesa.csproj -c Release -r win-x64 --self-contained true -o publish
```

The executable is `publish/MPesa.exe` and does not require a separate .NET installation.

The executable, window, tray icon, Desktop shortcut, and installer use the M-Pesa icon from `mpesa.ico`.

The application uses SQL authentication against `Server\MSSQLServer`, database `Restaurant`, with the supplied `sa` credentials in the published configuration. Change the password in `publish/appsettings.json` if it is different on the target server. The window and notification-area icon use M-Pesa branding; closing the window minimizes it to the tray, while `Exit` from the tray menu stops the application.

Use `Insert Dummy Messages` from the window to insert three test transactions into the database.

Use `Preview Parser` to inspect a sample confirmation without changing the database or modem. Automated parser tests can be run with:

```text
dotnet test MPesa.Tests/MPesa.Tests.csproj -c Release
```

The `Settings` tab is protected by the administrator password `123456`. It allows the server/instance, database, SQL user, and password to be changed without editing JSON manually. Database changes are masked while entered and take effect after restarting the application. Settings are saved per Windows user under `%LOCALAPPDATA%\MPesa\appsettings.json`, so saving does not require write access to `C:\Program Files`.

## Installer

`MPesaInstaller.iss` is an Inno Setup installer definition. Open it with Inno Setup and click **Compile** to create `installer\MPesaSetup.exe`.

If Inno Setup is not installed, run `Install-MPesa.cmd` as Administrator. It offers to create/update the database after copying the files:

```powershell
Install-MPesa.cmd
```

This fallback copies the published application to `C:\Program Files\MPesa`, creates Start Menu and Desktop shortcuts, offers database setup, and offers Windows-startup registration. The Inno Setup installer provides the same optional startup task and runs `Setup-Database.ps1` after installation.

The GSM driver cannot be bundled generically because each modem manufacturer supplies a different driver. Install the driver package for the exact modem model first, then connect the modem and let the app discover its COM port.

For a simpler installation on another Windows computer, copy the complete project folder, including `publish`, and double-click `Install-MPesa.cmd`. It requests Administrator permission automatically and runs PowerShell with execution-policy bypass. Do not copy only the `.ps1` file; the `publish` folder must remain beside the installer files.
# Mpesa-Puller
