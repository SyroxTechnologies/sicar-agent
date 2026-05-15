# Stockandria SICAR Agent

Agente local que se conecta al backend de Stockandria por WebSocket y ejecuta
comandos (sincronizar productos, ajustar stock, etc.) contra la DB
MariaDB/MySQL local de SICAR.

**Un agente por servidor MySQL de SICAR.** Si todas las sucursales del cliente
comparten servidor (varias DBs en el mismo MySQL), alcanza un agente. Si hay
servidores MySQL separados (típicamente una PC por sucursal), hace falta un
agente por servidor. En Stockandria cada sucursal se asocia a un nombre de DB
SICAR (`sicar_norte`, `sicar_chihuahua`, etc.) y el backend le dice al agente
qué DB usar en cada comando.

---

## Dev local (líder / dev team)

### Requisitos

- .NET 8 SDK
- Backend Stockandria corriendo en `http://localhost:5010` (con sus dependencias: PostgreSQL + Redis)
- Front cliente para generar el token de vinculación
- Acceso a un MySQL/MariaDB con DB SICAR

### Pasos

1. Front cliente → Sucursales → integración → **Generar token de vinculación** → copiar hex.

2. Exportar variables y arrancar:

   ```bash
   cd /path/to/sicar-agent
   export DOTNET_ENVIRONMENT=Development
   export STOCKANDRIA_LINK_TOKEN="<hex-de-64>"
   export STOCKANDRIA_SICAR_BASE_CONNECTION_STRING="Server=localhost;Port=3306;Uid=root;Pwd=TU_PASS;"
   export STOCKANDRIA_SICAR_DATABASE_NAME="sicar_norte"
   dotnet run --project src/StockandriaAgent
   ```

   > Para apuntar a backend deployado: en lugar de `DOTNET_ENVIRONMENT`, usá `export Backend__Url="https://api.stockandria.cloud"`.
   > Si no seteás `STOCKANDRIA_SICAR_DATABASE_NAME`, el agente arranca un wizard interactivo y te lo pregunta.

3. En el front, la sucursal pasa a **"En línea"**. Probar con "Sincronizar proveedores" / "Sincronizar productos".

### Vincular una segunda sucursal al mismo agente

Ctrl+C, generá un token nuevo, exportá `STOCKANDRIA_LINK_TOKEN` y
`STOCKANDRIA_SICAR_DATABASE_NAME` con los nuevos valores, `dotnet run` otra vez.
El agente conserva su `installationId` y suma la sucursal sin perder las anteriores.

### Empezar de cero

```bash
# Linux/macOS
rm ~/.config/StockandriaAgent/config.dat
# Windows
Remove-Item "$env:ProgramData\StockandriaAgent\config.dat"
```

### Problemas comunes

| Síntoma                                         | Solución                                                     |
| ----------------------------------------------- | ------------------------------------------------------------ |
| `ECONNREFUSED` al arrancar                      | El backend no responde — verificar que esté corriendo en `Backend:Url` |
| `Token de vinculación inválido`                 | Generar uno nuevo (single-use, expira en 60 min)             |
| `SicarBaseConnectionString no está configurada` | Re-exportar la env var en la misma terminal                  |
| Comandos en `TIMEOUT`                           | Revisar `logs/agent-YYYYMMDD.log`                            |

### Arquitectura

```
[PC dev]                                    [Mismo PC o remoto]
├─ Stockandria-Back (NestJS :5010)          ├─ MySQL con N DBs SICAR
├─ PostgreSQL                               │   (sicar_norte, sicar_chihuahua...)
├─ Redis                                    │
├─ Stockandria-Front (:3000)                │
└─ StockandriaAgent ◀──── Socket.io ────────┤
                                             │
         └─ SELECT / UPDATE sobre la DB que
            el back indique en cada comando
```

Regla estricta: **solo SELECT y UPDATE** sobre SICAR. Nunca INSERT ni DELETE.
La auditoría vive en Stockandria (`agent_commands`).

---

## Para el cliente (producción Windows)

### Pre-requisitos

- Windows 10/11 o Server con permisos de admin.
- SICAR instalado y corriendo (con su MySQL/MariaDB local).
- Datos del MySQL de SICAR: host, puerto, usuario, contraseña.
- Acceso a `https://app.stockandria.cloud`.

### Paso 1 — Generar el token de vinculación en Stockandria

1. Entrar a `https://app.stockandria.cloud`.
2. **Sucursales** → click en la sucursal → ícono de integración (cadena).
3. **Generar token de vinculación** → copiar el hex de 64 caracteres.

### Paso 2 — Configurar y arrancar el agente

1. Descomprimir el ZIP en `C:\Stockandria\Agent\`.
2. Abrir **PowerShell como administrador**.
3. Setear las variables de entorno (persistentes en la PC):

   ```powershell
   [Environment]::SetEnvironmentVariable("Backend__Url", "https://api.stockandria.cloud", "Machine")
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_SICAR_BASE_CONNECTION_STRING", "Server=localhost;Port=3306;Uid=root;Pwd=PASS_DE_SICAR;", "Machine")
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", "PEGAR_TOKEN_AQUI", "Machine")
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_SICAR_DATABASE_NAME", "NOMBRE_DB_SICAR", "Machine")
   ```

4. **Cerrar y volver a abrir PowerShell** (necesario para que tome las variables).
5. Arrancar el agente:

   ```powershell
   cd C:\Stockandria\Agent
   .\StockandriaAgent.exe
   ```

6. En Stockandria la sucursal pasa a **"SICAR conectado"**. Cerrar el agente con **Ctrl+C**.

### Paso 3 — Convertir el agente en servicio Windows

Para que arranque solo y corra en background:

```powershell
sc.exe create StockandriaAgent binPath= "C:\Stockandria\Agent\StockandriaAgent.exe" start= auto DisplayName= "Stockandria SICAR Agent"
sc.exe start StockandriaAgent
```

Verificar que diga `STATE: 4 RUNNING`:

```powershell
sc.exe query StockandriaAgent
```

### Agregar nueva sucursal

1. **Stockandria** → Sucursales → click en la sucursal nueva → integración → **Generar token** → copiar.

2. **PowerShell admin** (cualquier directorio):

   ```powershell
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", "PEGAR_TOKEN_NUEVO", "Machine")
   [Environment]::SetEnvironmentVariable("STOCKANDRIA_SICAR_DATABASE_NAME", "NOMBRE_DB_NUEVA", "Machine")
   ```

3. Reiniciar el servicio:

   ```powershell
   sc.exe stop StockandriaAgent
   sc.exe start StockandriaAgent
   ```

4. En Stockandria, la nueva sucursal pasa a **"SICAR conectado"**.


### Operación diaria

| Acción      | Comando                                                                 |
| ----------- | ----------------------------------------------------------------------- |
| Estado      | `sc.exe query StockandriaAgent`                                         |
| Detener     | `sc.exe stop StockandriaAgent`                                          |
| Iniciar     | `sc.exe start StockandriaAgent`                                         |
| Logs        | `C:\Stockandria\Agent\logs\agent-YYYYMMDD.log`                          |
| Desinstalar | `sc.exe stop StockandriaAgent` y luego `sc.exe delete StockandriaAgent` |

### Si algo falla

| Síntoma                  | Cómo investigar                                                                              |
| ------------------------ | -------------------------------------------------------------------------------------------- |
| Sucursal queda offline   | `sc.exe query StockandriaAgent`. Si está corriendo, abrir el log del día.                    |
| Sync en `TIMEOUT`        | Verificar que SICAR/MySQL esté corriendo. Probar el connection string desde MySQL Workbench. |
| `Token inválido`         | Generar uno nuevo (los tokens son single-use y duran 60 min).                                |
| Cambió la pass del MySQL | Resetear `STOCKANDRIA_SICAR_BASE_CONNECTION_STRING` y reiniciar el servicio.                 |

Para soporte: enviar el log del día (`logs\agent-YYYYMMDD.log`).
