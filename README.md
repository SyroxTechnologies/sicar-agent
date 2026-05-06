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

#### Arrancar el agente

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
export STOCKANDRIA_SICAR_BASE_CONNECTION_STRING="Server=localhost;Port=3306;Uid=root;Pwd=TU_PASS;"

# Nombre de la DB SICAR de la sucursal que vas a vincular.
# Si no lo seteás, el agente arranca un wizard interactivo y te lo pregunta.
export STOCKANDRIA_SICAR_DATABASE_NAME="sicar_norte"
```

> Para apuntar el agente dev a un backend deployado en lugar de localhost, en vez de `DOTNET_ENVIRONMENT=Development` usá `export Backend__Url="https://api.stockandria.cloud"`.

### 3. Corré el agente

```bash
dotnet run --project src/StockandriaAgent
```

#### Diferencias entre Linux/macOS y Windows

- **Variables de entorno**: Linux/macOS usa `export VAR=valor`. Windows (PowerShell) usa `$env:VAR = "valor"`.
- **Cifrado del `config.dat`**: Windows usa **DPAPI** (producción real, los datos nunca quedan legibles en disco). Linux/macOS usa **plaintext con permisos `0600`** — es modo dev, aparece un warning en los logs.
- **Instalar como servicio en Windows** (opcional, para que arranque con el sistema): desde PowerShell como administrador, en la raíz del repo:
  ```powershell
  .\install.ps1 -LinkToken "pega-el-token-aca"
  ```
  Desinstala con `.\uninstall.ps1`.

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

1. En el front de Stockandria, drawer de integración de la sucursal que
   vinculaste. La sucursal debe aparecer **"En línea"** y mostrar el
   `databaseName` que pasaste en `STOCKANDRIA_SICAR_DATABASE_NAME`.
2. En **"Enviar comando"** → "Probar conexión" → click "Enviar".
3. El comando pasa `PENDING → PICKED → RUNNING → SUCCESS` en 1-2 segundos.
4. Si llega a **SUCCESS**, todo el flujo funciona: hub + cola + agente + MySQL.
5. Repetí con "Sincronizar productos" / "Sincronizar proveedores" y vas a
   ver cargar los datos en `/inventario` y `/proveedores`.

Para vincular **una segunda sucursal al mismo agente** (en dev): Ctrl+C,
generás un link-token nuevo en el front, exportás `STOCKANDRIA_LINK_TOKEN`
y `STOCKANDRIA_SICAR_DATABASE_NAME` con los nuevos valores, y volvés a
correr `dotnet run`. El agente conserva su `installationId` y suma la
sucursal nueva sin perder las anteriores.

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

---

# Para cliente (producción Windows)

Esta sección es para el técnico del cliente que va a instalar y operar
el agente en su PC con SICAR. Apunta al backend deployado
(`https://api.stockandria.cloud`), no a un entorno local.

---

#### Pre-requisitos

1. **Windows 10/11 o Windows Server**.
2. **SICAR instalado** y corriendo en la PC (con su MySQL/MariaDB local).
3. **Permisos de administrador** en la PC.
4. **Saber el host, puerto, usuario y contraseña** del MySQL de SICAR.
   Si SICAR está instalado localmente, suele ser:
   - Host: `localhost`
   - Puerto: `3306`
   - Usuario y contraseña: los que se configuraron al instalar SICAR.
5. **Acceso a Stockandria** (`https://app.stockandria.cloud`) con un
   usuario que pueda generar tokens de vinculación.

---

#### Paso 1 — Instalar el agente (una sola vez)

1. Descomprimir el ZIP que te pasamos en una carpeta fija. Recomendado:
   `C:\Stockandria\Agent\`.

2. Abrir **PowerShell como administrador** (botón derecho → "Ejecutar como administrador").

3. Setear las **variables de entorno permanentes** (las copia el sistema y persisten al reiniciar la PC):

   ```powershell
   [Environment]::SetEnvironmentVariable("Backend__Url", "https://api.stockandria.cloud", "Machine")
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_SICAR_BASE_CONNECTION_STRING", "Server=localhost;Port=3306;Uid=root;Pwd=PONER_LA_PASS_DE_SICAR;", "Machine")
   ```

   > Reemplazar `localhost`, `3306`, `root` y `PONER_LA_PASS_DE_SICAR` por los datos reales del MySQL de SICAR del cliente.

4. **No arranques el agente todavía** — primero hay que vincular la primera sucursal (paso siguiente).

---

#### Paso 2 — Vincular la primera sucursal

1. Entrar a `https://app.stockandria.cloud` con tu usuario.
2. Ir a **Sucursales** → click en la sucursal que querés vincular → ícono de **integración** (cadena).
3. Click en **"Generar token de vinculación"** → copiar el hex de 64 caracteres.
4. En la **PowerShell admin** que ya tenés abierta:

   ```powershell
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", "PEGAR_EL_HEX_ACA", "Machine")
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_SICAR_DATABASE_NAME", "NOMBRE_DB_DE_LA_SUCURSAL", "Machine")
   ```

   > `NOMBRE_DB_DE_LA_SUCURSAL` es el nombre de la base de datos SICAR
   > de esa sucursal (lo ves en SICAR o consultando `SHOW DATABASES;`
   > en MySQL).

5. **Cerrar y volver a abrir la PowerShell** (necesario para que el proceso lea las variables nuevas).

6. Arrancar el agente la primera vez:

   ```powershell
   cd C:\Stockandria\Agent
   .\StockandriaAgent.exe
   ```

7. En la pantalla del agente vas a ver `Registro exitoso` y `Conectado al hub del backend`.
8. En Stockandria, el badge de la sucursal debe pasar a **verde "SICAR conectado"**.
9. Cerrar el agente con **Ctrl+C**.

---

#### Paso 3 — Convertir el agente en servicio Windows (corre permanente en background)

Con el agente cerrado, en la PowerShell admin:

```powershell
sc.exe create StockandriaAgent binPath= "C:\Stockandria\Agent\StockandriaAgent.exe" start= auto DisplayName= "Stockandria SICAR Agent"
sc.exe start StockandriaAgent
```

Verificar que está corriendo:

```powershell
sc.exe query StockandriaAgent
```

Tiene que decir `STATE: 4 RUNNING`. A partir de acá el agente arranca solo cuando prende la PC. **No hace falta tener PowerShell abierta**.

> El token ya se consumió en el paso 2 (es single-use). Por prolijidad,
> podés borrar la env var:
> ```powershell
> [Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", $null, "Machine")
> ```

---

#### Vincular sucursales adicionales

Cada vez que el cliente quiere vincular una **sucursal nueva** al mismo agente:

1. **Stockandria** → generar link-token de la nueva sucursal (igual que paso 2.1-2.3).
2. **PowerShell admin**:

   ```powershell
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", "PEGAR_NUEVO_HEX", "Machine")
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_SICAR_DATABASE_NAME", "NOMBRE_DB_NUEVA", "Machine")
   ```

3. **Reiniciar el servicio**:

   ```powershell
   sc.exe stop StockandriaAgent
   sc.exe start StockandriaAgent
   ```

4. Verificar en Stockandria que la nueva sucursal pasa a **ONLINE**.

5. (Opcional) Limpiar el token consumido:

   ```powershell
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", $null, "Machine")
   ```

> **Importante**: el agente sigue atendiendo las sucursales que vinculaste antes. La conexión al MySQL **se mantiene la misma** — todas las DBs tienen que estar en el mismo servidor MySQL configurado en `STOCKANDRIA_SICAR_BASE_CONNECTION_STRING`.

---

#### Operación diaria

| Acción | Comando PowerShell admin |
|---|---|
| Ver estado del agente | `sc.exe query StockandriaAgent` |
| Detener el agente | `sc.exe stop StockandriaAgent` |
| Iniciar el agente | `sc.exe start StockandriaAgent` |
| Ver logs | abrir `C:\Stockandria\Agent\logs\agent-YYYYMMDD.log` |
| Desinstalar el servicio | `sc.exe stop StockandriaAgent` y luego `sc.exe delete StockandriaAgent` |

---

#### Si algo falla

| Síntoma | Cómo investigar |
|---|---|
| Sucursal queda offline en Stockandria | Verificar `sc.exe query StockandriaAgent`. Si está corriendo, abrir el log del día. |
| Sync queda en `TIMEOUT` | Verificar que SICAR/MySQL esté corriendo en la PC. Probar el connection string desde MySQL Workbench. |
| `Token de vinculación inválido` | Generar un token nuevo (los tokens duran 60 min y son single-use). |
| Cambió la contraseña del MySQL de SICAR | Reseteá la env var `STOCKANDRIA_SICAR_BASE_CONNECTION_STRING` con la nueva pass y reiniciá el servicio. |

