# Сторонние компоненты

В поставку входят файлы Microsoft .NET Runtime и Windows Desktop Runtime 10.0.12. Их лицензии и уведомления третьих лиц находятся в каталоге `licenses` установленного приложения. Сборочный скрипт копирует оригинальные тексты из соответствующих NuGet runtime-пакетов.

- .NET runtime: https://github.com/dotnet/runtime
- WPF: https://github.com/dotnet/wpf
- Windows Forms (NotifyIcon): https://github.com/dotnet/winforms
- NSIS: https://nsis.sourceforge.io/License

NSIS используется для установщика; соответствующий текст лицензии включён при наличии его в установленном NSIS. Системная `winsqlite3.dll` Windows не поставляется отдельно. В Linux-тестах применяется системная SQLite; она не входит в Windows-пакет проекта.

Иконки Family Time созданы для этой сборки. Дополнительные JavaScript-библиотеки и внешние шрифты не используются.
