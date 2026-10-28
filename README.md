# MiniCut

Editor de video de escritorio (WPF, .NET 8) inspirado en CapCut. Uso educativo.

## Requisitos
- Windows 10/11, .NET 8 SDK
- ffmpeg y ffprobe (`winget install Gyan.FFmpeg`) — solo para importar (miniaturas/duración) y exportar

## Ejecutar
```
dotnet run
```

## Qué tiene
- Vista previa en tiempo real: reproduce los archivos originales y compone en un lienzo, con filtros y efectos por shader de GPU (HLSL compilado con D3DCompile)
- Panel de medios: clic = vista previa del medio; arrastrarlo a la línea de tiempo = editarlo
- 5 pistas (texto, 2 de video, 2 de audio), arrastrar/recortar/dividir, imán a bordes
- Clips con tira de miniaturas por escenas y forma de onda de audio; línea de volumen arrastrable en dB (clip seleccionado)
- Keyframes (posición, escala, rotación, volumen) con interpolación lineal
- Mezclador de audio propio (NAudio): el volumen en dB, keyframes y fundidos se oyen en la vista previa igual que en la exportación
- Filtros, efectos, ajustes de color, velocidad, volumen, fundidos, texto
- Deshacer/rehacer, guardar/abrir proyectos `.minicut`
- Exportación MP4 con ffmpeg (formatos 16:9, 9:16, 1:1, 4:3)

## Modo sin ventana
```
MiniCut.exe --render proyecto.minicut salida.mp4
MiniCut.exe --frame proyecto.minicut salida.png <segundos>
```

## Estructura
- `Engine.cs` — modelo (Seg, Kf, Project) y generador del grafo de ffmpeg para exportar
- `Grade.cs` — shader de color/efectos en tiempo real
- `MainWindow.xaml(.cs)` — interfaz, línea de tiempo, compositor en vivo


