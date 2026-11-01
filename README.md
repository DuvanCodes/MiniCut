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
- Pantalla de inicio estilo CapCut: crear proyecto, formatos (16:9, 9:16, 1:1, 4:3), proyectos recientes con portada, papelera y plantillas propias; los proyectos se autoguardan en `%LOCALAPPDATA%\MiniCut\Projects`
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




## Hallazgos de ingeniería inversa usados (v0.7)
- **Modos de mezcla**: `Resources/MixMode/*` de CapCut contiene 10 shaders GLSL (multiply, screen, overlay, darken, lighten, hard light, soft light, linear burn, color burn, color dodge). Todos componen en alfa recto: `out = fg·fa·(1−ba) + bg·ba·(1−fa) + fa·ba·B(fg,bg)`. MiniCut usa esas fórmulas en `Blend.cs` (vista previa) y en `Opts.Blends` (ffmpeg `blend`).
- **Máscaras**: `videoeditor.dll` expone los tipos `linear`, `circle`, `rectangle`, `heart` (+ espejo) y las propiedades `MaskSize{Width,Height}`, `MaskRotation`, `MaskFeather`, `MaskRoundCorner` (claves de keyframe `KFTypeCommonMask*`). MiniCut las replica en `Grade.cs` y `Seg.MaskExpr()`.
- **Cabecera de pista**: ojo / altavoz / candado, como en CapCut (`Project.Hidden/Muted/Locked`).

## Bibliotecas del usuario (v0.8)
MiniCut no descarga nada de CapCut. Cada persona añade su propio contenido:
- **Filtros**: pestaña Filtros → *Mis filtros* → importar LUT `.cube` 3D (clic derecho para eliminar). Se guardan en `%LOCALAPPDATA%\MiniCut\Luts`.
- **Música**: pestaña Audio → *Mi biblioteca* → elegir una carpeta (se escanean mp3/wav/m4a/aac/ogg/flac).
- **Fuentes**: panel Texto → Fuente → *＋ Fuente…* (`.ttf`/`.otf`), guardadas en `%LOCALAPPDATA%\MiniCut\Fonts`.

## Audio y velocidad (v0.10)
- **Tono** (±12 semitonos), **cambio de voz** (11 efectos sin IA) y **cambiar tono con la velocidad**: cadena de filtros ffmpeg (`Ff.AudioChain`). La vista previa reproduce una copia procesada del audio decodificado; la exportación aplica la misma cadena.
- **Curvas de velocidad** y **efectos de velocidad**: se aplican cortando el clip en 12 trozos de velocidad constante (preview y exportación coinciden). Dev hook: `MINICUT_CURVE=<nombre>`.
