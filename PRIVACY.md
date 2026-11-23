# GPOCasebook privacy policy

Effective November 23, 2026.

GPOCasebook is a Windows desktop application published by Alireza Foroughifar. It helps you investigate Windows Group Policy by reading evidence you select or are authorized to access. This policy describes the information the app handles during normal use.

## Information the app can handle

Depending on the actions you choose, evidence can include:

- computer, domain, and Windows user names present in logs
- Group Policy event messages, record numbers, channels, timestamps, Activity IDs, and policy names
- file paths and reported Group Policy or RSoP results
- the contents of event XML, EVTX exports, and gpresult /x XML files you import

## Collection and storage

- **Local collection** reads Group Policy event channels on the current computer and requests an RSoP report through the Windows gpresult tool.
- **Imports** read only files you select.
- **Domain scans** use authenticated Windows directory, Event Log, and Computer RSoP queries against the domain and computers you select.
- **Alternate credentials** are handled by the Windows Run as prompt. GPOCasebook does not receive or store the password entered for that Windows logon session.
- Captures, archives, scan summaries, and exported reports are stored under your Windows profile or at the destination you choose.

The app does not require an account and sends no publisher telemetry during routine use. Windows and Microsoft services may process data according to their own settings when you use them.

## Sharing

The default shareable HTML report omits named computers, named policies, and raw event fields. Detailed reports, domain scan summaries, archive files, and imported evidence can contain internal troubleshooting data. Review every file before sending it to another person or attaching it to a public issue.

You control access to local archives and can delete archive files or exports. GPOCasebook does not edit Group Policy, install a background service, or store Windows passwords.

## Contact

For privacy questions or support, open an issue at [github.com/afrfuniverse/gpocasebook/issues](https://github.com/afrfuniverse/gpocasebook/issues). Do not include confidential logs or credentials in a public issue.
