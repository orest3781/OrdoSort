# ============= CONFIGURATION SECTION =============
# Set the folders you want to process (one per line).
# Point these at your own share before running — the paths below are
# placeholders, not a live location.
$folderPaths = @(
    "\\SERVER\SHARE\CD",
    "\\SERVER\SHARE\EMAILS_MR",
    "\\SERVER\SHARE\EMAILS_APPEAL"
)

# Set to $true to process subfolders recursively, $false to only process the main folders
$recursive = $true

# Log file location (will be created in the same folder as the script by default)
$logFileName = "ReadOnlyRemoval_Log.txt"
# ================================================

# Get the script's directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFilePath = Join-Path -Path $scriptDir -ChildPath $logFileName

# Initialize variables
$filesToProcess = @()
$fileCount = 0

# First phase: Scan all folders for read-only files without logging
foreach ($folderPath in $folderPaths) {
    if (Test-Path -Path $folderPath -PathType Container) {
        # Get files based on recursive setting
        if ($recursive) {
            # Get all files in the folder and all subfolders
            $files = Get-ChildItem -Path $folderPath -File -Recurse -ErrorAction SilentlyContinue
        } else {
            # Get only files in the current folder (no recursion)
            $files = Get-ChildItem -Path $folderPath -File -ErrorAction SilentlyContinue
        }
        
        # Find read-only files
        foreach ($file in $files) {
            if ($file.IsReadOnly) {
                $filesToProcess += $file
            }
        }
    }
}

# Second phase: If files need processing, write to log and remove read-only attribute
if ($filesToProcess.Count -gt 0) {
    # Function to write to log file
    function Write-Log {
        param([string]$message)
        Add-Content -Path $logFilePath -Value $message
    }
    
    # Add separator to existing log if it exists, otherwise create new log file
    if (Test-Path -Path $logFilePath) {
        Add-Content -Path $logFilePath -Value ""
        Add-Content -Path $logFilePath -Value "===================================================="
        Add-Content -Path $logFilePath -Value "NEW LOG SESSION STARTED"
        Add-Content -Path $logFilePath -Value "===================================================="
    }
    
    # Get current username and UTC date/time
    $currentUsername = $env:USERNAME
    $currentDateTime = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss")
    
    # Write header to log file
    Write-Log "=== READ-ONLY ATTRIBUTE REMOVAL LOG ==="
    Write-Log "Started: $currentDateTime (UTC)"
    Write-Log "User: $currentUsername"
    Write-Log "Recursive Mode: $($recursive.ToString().ToUpper())"
    Write-Log "==============================================="
    
    # Process each file
    foreach ($file in $filesToProcess) {
        # Remove read-only attribute
        Set-ItemProperty -Path $file.FullName -Name IsReadOnly -Value $false
        $fileCount++
        Write-Log "Removed read-only from: $($file.FullName)"
    }
    
    # Write summary to log file
    $summary = if ($fileCount -eq 1) {
        "Read-only attribute removed from 1 file."
    } else {
        "Read-only attribute removed from $fileCount files."
    }
    
    Write-Log ""
    Write-Log "==============================================="
    Write-Log "Operation completed"
    Write-Log $summary
    Write-Log "=== END OF LOG ==="
}

# No output or logging if no files were changed