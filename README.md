# Stockandria SICAR Agent

Agente local que se conecta al backend de Stockandria por WebSocket y ejecuta
comandos (sincronizar productos, ajustar stock, etc.) contra la DB
MariaDB/MySQL local de SICAR.

**Un solo agente por organización**, aunque el cliente tenga varias sucursales.
El agente se conecta al servidor MySQL base; en Stockandria cada sucursal se
asocia a un nombre de DB SICAR (`sicar_norte`, `sicar_chihuahua`, etc.) y el
backend le dice al agente qué DB usar en cada comando.

---

#### Requisitos previos

Antes de arrancar el agente, necesitás tener **ya corriendo** en tu máquina:

1. **Backend Stockandria** (`Stockandria-Back`) en `http://localhost:5010`.
2. **Redis** (lo usa el backend para la cola de comandos).
3. **PostgreSQL** con la DB de Stockandria migrada.
4. **Front admin Stockandria** (para generar el link-token de vinculación).
5. **Acceso a una DB MySQL/MariaDB de SICAR** — puede ser local, de prueba, o
   una copia en un servidor remoto.
---

#### Arrancar el agente — Linux / macOS

Requiere [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

### 1. Generá un link-token desde el admin

- Desde /admin/sucursales → si no hay sucursales creadas, creá una 
- En cualquier sucursal, click en el ícono de integración (cadena) → **"Generar token de vinculación"**.
- Copiás el hex de 64 caracteres. Expira en 60 minutos y se usa una sola vez.

### 2. Exportá las variables de entorno

En la raíz del proyecto:

```bash
# Modo dev: usa appsettings.Development.json que apunta a localhost:5010
export DOTNET_ENVIRONMENT=Development

# Token que acabás de generar
export STOCKANDRIA_LINK_TOKEN="pega-el-token-aca"

# Conexión al MySQL de SICAR (¡SIN Database= al final!)
# Ejemplo con MySQL local:
export STOCKANDRIA_SICAR_BASE_CONNECTION_STRING="Server=localhost;Port=3306;Uid=root;Pwd=TU_PASS;"
```

### 3. Corré el agente

```bash
dotnet run --project src/StockandriaAgent
```

> **En Linux/macOS**: como DPAPI solo existe en Windows, el agente guarda el
> `config.dat` en **plaintext** con permisos `0600`. Queda el warning en los
> logs — es modo dev, no apto para producción.

---

#### Arrancar el agente — Windows

Igual que Linux pero con sintaxis PowerShell. Requiere también [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
$env:STOCKANDRIA_LINK_TOKEN = "pega-el-token-aca"
$env:STOCKANDRIA_SICAR_BASE_CONNECTION_STRING = "Server=localhost;Port=3306;Uid=root;Pwd=TU_PASS;"

dotnet run --project src/StockandriaAgent
```

En Windows usa **DPAPI** para cifrar el token y la connection string — eso es
producción real (los datos nunca quedan legibles en disco).

### Opcional — instalar como servicio de Windows

Para que el agente arranque con el sistema y quede corriendo en segundo plano
permanentemente (como haría un cliente en producción):

```powershell
# En PowerShell como Administrador, desde la raíz del repo:
.\install.ps1 -LinkToken "pega-el-token-aca"
```

El script compila el binario self-contained, lo copia a
`C:\Program Files\StockandriaAgent\`, y crea el servicio `StockandriaAgent`.

Para desinstalar:
```powershell
.\uninstall.ps1
```

---

#### Qué esperás ver al arrancar

```
[INF] Sin configuración previa en ... Iniciando flujo de registro.
[INF] SicarBaseConnectionString leída desde STOCKANDRIA_SICAR_BASE_CONNECTION_STRING.
[INF] Bases de datos SICAR detectadas: sicar_norte, sicar_chihuahua, ...
[INF] Registrando agente contra http://localhost:5010 como NOMBRE-PC
[INF] Registro exitoso: agentId=cm..., orgId=cm...
[INF] Conectado al hub del backend
```

---

#### Cómo probar que funciona

1. En el admin de Stockandria, entrás al drawer de integración de una
   sucursal. El agente debería aparecer **"En línea"**.
2. En el bloque **"Base de datos SICAR"**, elegí del dropdown qué DB
   corresponde a esa sucursal (ej: `sicar_norte`).
3. Click "Guardar".
4. En **"Enviar comando"** → "Probar conexión" → click "Enviar".
5. El comando pasa `PENDING → PICKED → RUNNING → SUCCESS` en 1-2 segundos.
6. Si llega a **SUCCESS**, todo el flujo funciona: hub + cola + agente + MySQL.
7. Repetí con "Sincronizar productos" y vas a ver cargar los productos en
   `/inventario`.

---

#### Problemas comunes

| Síntoma | Causa probable | Solución |
|---------|----------------|----------|
| `ECONNREFUSED` al arrancar | Redis no está corriendo | Levantá Redis (`docker run -d -p 6379:6379 redis:7-alpine`) antes de arrancar el backend |
| `404 Not Found` en `/agent/register` | Backend mal o no está en localhost:5010 | Verificá que el backend esté corriendo. El front usa otro puerto (3000), no confundir. |
| `Token de vinculación inválido` | Link-token ya usado o expirado | Generá uno nuevo desde el admin |
| `SicarBaseConnectionString no está configurada` | Faltó exportar la env var | Volvé a exportar `STOCKANDRIA_SICAR_BASE_CONNECTION_STRING` en esa misma terminal |
| Comandos quedan en `TIMEOUT` | MySQL no responde o query lenta | Revisá los logs en `logs/agent-YYYYMMDD.log` |
| `Unknown column 'xxx'` en SYNC | El schema de SICAR de este cliente difiere | Pegar el error en el canal de trabajo — hay que ajustar un alias en el query |

---

#### Empezar de cero

Si querés reiniciar la vinculación con un link-token nuevo:

**Linux/macOS:**
```bash
rm ~/.config/StockandriaAgent/config.dat
```

**Windows:**
```powershell
Remove-Item "$env:ProgramData\StockandriaAgent\config.dat"
```

Después volvés a correr con un link-token nuevo y el wizard te vuelve a
preguntar datos si hace falta.

---

#### Arquitectura resumida

```
[Tu PC de desarrollo]                         [Mismo PC o remoto]
├─ Stockandria-Back (NestJS :5010)            ├─ MySQL con N DBs SICAR
├─ PostgreSQL                                 │   (sicar_norte, sicar_chihuahua...)
├─ Redis                                      │
├─ Stockandria-Front (:3000)                  │
└─ StockandriaAgent ◀────── Socket.io ────────┤
                                               │
         └─ SELECT / UPDATE sobre la DB SICAR
            que el backend indique en cada
            comando (payload.databaseName)
```

Regla estricta en SICAR: **solo SELECT y UPDATE**. Nunca INSERT ni DELETE.
La auditoría vive en Stockandria (tabla `agent_commands`), no en SICAR.
