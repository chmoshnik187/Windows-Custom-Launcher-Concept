# Сторонние компоненты

DotShell собирается в один self-contained exe: всё перечисленное ниже уже внутри
исполняемого файла, отдельно ставить ничего не нужно.

## Deskband11Lib

- Пакет: `Deskband11Lib.Wpf` 1.5.1 (транзитивно `Deskband11Lib.Core`)
- Автор: Howon Lee (airtaxi), https://github.com/airtaxi/Deskband11Lib
- Лицензия: MIT
- Что делает: превращает окно DotShell в `WS_CHILD`-ребёнка `Shell_TrayWnd` /
  `Shell_SecondaryTrayWnd`, считает свободное место в панели задач через UI
  Automation, обрезает окно по этому месту, переживает смену положения панели,
  DPI, отключение монитора и перезапуск explorer. Именно это даёт настоящую
  встроенность острова в панель, а не оверлей поверх неё.

Текст лицензии MIT:

```
MIT License

Copyright (c) Deskband11Lib contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## .NET Runtime

Self-contained сборка включает .NET 10 Desktop Runtime; пользователю не нужно
устанавливать .NET отдельно.
