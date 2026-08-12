param(
    # Pass your own mailbox and destination share — the defaults below are
    # placeholders, not a live account or location.
    [Parameter(Mandatory = $false)]
    [string]$EmailAddress = "user@example.com",

    [Parameter(Mandatory = $false)]
    [string]$OutlookFolderPath = "CAVO AUTOMATION REPORT",

    [Parameter(Mandatory = $false)]
    [string]$DownloadPath = "\\SERVER\SHARE\CAVO_REPORTS",
    
    [Parameter(Mandatory = $false)]
    [switch]$OnlyUnreadEmails,
    
    [Parameter(Mandatory = $false)]
    [switch]$MarkAsRead,
    
    [Parameter(Mandatory = $false)]
    [int]$TimeoutMinutes = 10
)

# Define helper functions
Function Find-Folder {
    param (
        [Parameter(Mandatory = $true)]
        [object]$StartFolder,
        [string]$TargetFolderPath
    )

    # If looking for root folder or empty path, return start folder
    if ([string]::IsNullOrWhiteSpace($TargetFolderPath) -or $TargetFolderPath -eq $StartFolder.Name) {
        return $StartFolder
    }

    # Split the path into components
    $folderParts = $TargetFolderPath -split '\\'
    
    # Start with the current folder
    $currentFolder = $StartFolder
    
    # Navigate through each part of the path
    foreach ($part in $folderParts) {
        if ([string]::IsNullOrWhiteSpace($part)) {
            continue
        }
        
        # If current folder name matches, continue to next part
        if ($currentFolder.Name -eq $part) {
            continue
        }
        
        # Look for the folder in the current folder's subfolders
        $found = $false
        try {
            foreach ($subFolder in $currentFolder.Folders) {
                if ($subFolder.Name -eq $part) {
                    $currentFolder = $subFolder
                    $found = $true
                    break
                }
            }
        }
        catch {
            Write-Warning "Error accessing subfolders: $($_.Exception.Message)"
            return $null
        }
        
        # If we didn't find the folder part, return null
        if (-not $found) {
            return $null
        }
    }
    
    return $currentFolder
}

Function Write-Retry {
    param (
        [Parameter(Mandatory = $true)]
        [ScriptBlock]$ScriptBlock,
        [int]$MaxRetries = 5,
        [int]$DelaySeconds = 2
    )

    $attempt = 0
    while ($attempt -lt $MaxRetries) {
        try {
            & $ScriptBlock
            return $true
        } catch {
            $attempt++
            if ($attempt -ge $MaxRetries) {
                throw
            }
            Start-Sleep -Seconds $DelaySeconds
        }
    }
    return $false
}

# Main function
Write-Host "Starting Outlook attachment downloader" -ForegroundColor Cyan
Write-Host "Processing folder: $OutlookFolderPath" -ForegroundColor Cyan
Write-Host "Saving attachments to: $DownloadPath" -ForegroundColor Cyan
Write-Host "Processing " -NoNewline
if ($OnlyUnreadEmails) {
    Write-Host "only unread emails" -ForegroundColor Yellow -NoNewline
} else {
    Write-Host "all emails" -ForegroundColor Yellow -NoNewline
}
Write-Host " in the folder"

# Ensure the paths are not empty
if ([string]::IsNullOrWhiteSpace($DownloadPath)) {
    $DownloadPath = "$env:USERPROFILE\Downloads\Email_Attachments"
    Write-Host "Download path was empty. Using default: $DownloadPath" -ForegroundColor Yellow
}

# Ensure the download path exists
try {
    if (-not (Test-Path -Path $DownloadPath)) {
        New-Item -ItemType Directory -Path $DownloadPath -Force | Out-Null
        Write-Host "Created download directory: $DownloadPath" -ForegroundColor Green
    }
} catch {
    Write-Host "Error creating download directory. Using Documents folder instead." -ForegroundColor Red
    $DownloadPath = "$env:USERPROFILE\Documents\Email_Attachments"
    New-Item -ItemType Directory -Path $DownloadPath -Force -ErrorAction SilentlyContinue | Out-Null
}

try {
    # Initialize Outlook application
    $outlook = New-Object -ComObject Outlook.Application
    $namespace = $outlook.GetNamespace("MAPI")
    
    # Find the account folder based on email address
    $rootFolder = $null
    if ([string]::IsNullOrWhiteSpace($EmailAddress)) {
        # If no email address specified, use default account
        $inbox = $namespace.GetDefaultFolder(6)  # 6 is the value for olFolderInbox
        $rootFolder = $inbox.Parent
        $EmailAddress = $rootFolder.Name
        Write-Host "Using default email account: $EmailAddress" -ForegroundColor Yellow
    } else {
        # Try to find the specified email account
        for ($i = 1; $i -le $namespace.Folders.Count; $i++) {
            $folder = $namespace.Folders.Item($i)
            if ($folder.Name -like "*$EmailAddress*") {
                $rootFolder = $folder
                Write-Host "Found email account: $($folder.Name)" -ForegroundColor Green
                break
            }
        }
        
        if ($null -eq $rootFolder) {
            throw "Could not find email account containing '$EmailAddress'. Please check the email address."
        }
    }
    
    # Find the target folder
    $targetFolder = if ($OutlookFolderPath -eq "Inbox") {
        $namespace.GetDefaultFolder(6)  # Default Inbox
    } else {
        Find-Folder -StartFolder $rootFolder -TargetFolderPath $OutlookFolderPath
    }
    
    if ($null -eq $targetFolder) {
        throw "The folder '$OutlookFolderPath' was not found in account $($rootFolder.Name)."
    }
    
    Write-Host "Successfully connected to Outlook and found folder: $($targetFolder.Name)" -ForegroundColor Green
    $emailCount = $targetFolder.Items.Count
    Write-Host "Found $emailCount total emails in folder"

    # Start the stopwatch to track elapsed time
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $processedCount = 0
    $downloadedCount = 0
    $downloadedEmails = 0

    # Loop through all items in the folder
    foreach ($item in $targetFolder.Items) {
        # Check if the timeout has been reached
        if ($stopwatch.Elapsed.TotalMinutes -ge $TimeoutMinutes) {
            Write-Host "Timeout reached after $TimeoutMinutes minutes. Ending script." -ForegroundColor Yellow
            break
        }

        try {
            $processedCount++
            
            # Skip if we only want unread emails and this one is read
            if ($OnlyUnreadEmails -and -not $item.UnRead) {
                continue
            }
            
            # Check if the item is an email
            if ($item.Class -eq 43) {  # 43 corresponds to olMail
                $subject = $item.Subject
                $senderName = $item.SenderName
                $receivedTime = $item.ReceivedTime
                $hasAttachments = $false
                
                # Skip if email is already marked as Downloaded
                if ($item.Categories -like "*Downloaded*") {
                    Write-Host "[$processedCount/$emailCount] Skipping already downloaded email: $subject" -ForegroundColor Yellow
                    continue
                }
                
                # Skip if there are no attachments
                if ($item.Attachments.Count -eq 0) {
                    continue
                }
                
                # Create date folder based on email received date
                $emailDate = Get-Date $receivedTime -Format "yyyyMMdd"
                $dateFolderPath = Join-Path -Path $DownloadPath -ChildPath $emailDate
                
                # Create date subfolder if it doesn't exist
                if (-not (Test-Path -Path $dateFolderPath)) {
                    New-Item -ItemType Directory -Path $dateFolderPath -Force | Out-Null
                }
                
                Write-Host "[$processedCount/$emailCount] Processing email: $subject - $(Get-Date $receivedTime -Format 'yyyy-MM-dd HH:mm')"

                # Loop through all attachments in the email
                foreach ($attachment in $item.Attachments) {
                    try {
                        $fileName = $attachment.FileName
                        
                        # Check if this is a Cavo Automation Report file and rename it
                        $newFileName = $fileName
                        if ($fileName -like "*Cavo_Automation_Report_PECF*") {
                            # Extract date and time from filename like "Cavo_Automation_Report_PECF_2025-05-09 0802.xlsx"
                            if ($fileName -match "PECF_(\d{4}-\d{2}-\d{2})\s(\d{4})\.(.+)$") {
                                $dateStr = $matches[1] -replace "-", ""  # Convert 2025-05-09 to 20250509
                                $timeStr = $matches[2]  # Keep 0802 as is
                                $extension = $matches[3]  # Get file extension
                                $newFileName = "$dateStr-$timeStr-PECF Report.$extension"
                                Write-Host "  Renaming: $fileName -> $newFileName" -ForegroundColor Cyan
                            }
                        }
                        
                        $filePath = Join-Path -Path $dateFolderPath -ChildPath $newFileName
                        
                        # Handle duplicate file names by adding a counter
                        $counter = 1
                        $originalFilePath = $filePath
                        while (Test-Path -Path $filePath) {
                            $fileExtension = [System.IO.Path]::GetExtension($newFileName)
                            $fileNameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($newFileName)
                            $duplicateFileName = "$fileNameWithoutExtension($counter)$fileExtension"
                            $filePath = Join-Path -Path $dateFolderPath -ChildPath $duplicateFileName
                            $counter++
                        }
                        
                        # Save the attachment directly to the date folder
                        $attachment.SaveAsFile($filePath)
                        $downloadedCount++
                        $hasAttachments = $true

                        Write-Host "  Saved: $(Split-Path $filePath -Leaf) to $emailDate folder"
                    } catch {
                        Write-Warning "Failed to save attachment '$($attachment.FileName)': $($_.Exception.Message)"
                    }
                }
                
                if ($hasAttachments) {
                    $downloadedEmails++
                    
                    # Add "Downloaded" category/tag to the email
                    try {
                        $item.Categories = "Downloaded"
                        $item.UnRead = $false
                        $item.Save()
                        Write-Host "  Tagged email as 'Downloaded' and marked as read" -ForegroundColor Green
                    } catch {
                        Write-Warning "Failed to tag email or mark as read: $($_.Exception.Message)"
                        
                        # Try to mark as read even if tagging fails
                        try {
                            $item.UnRead = $false
                            $item.Save()
                            Write-Host "  Marked email as read" -ForegroundColor Green
                        } catch {
                            Write-Warning "Failed to mark email as read: $($_.Exception.Message)"
                        }
                    }
                } elseif ($MarkAsRead) {
                    # Only mark as read if MarkAsRead switch is used and no attachments were processed
                    try {
                        $item.UnRead = $false
                        $item.Save()
                    } catch {
                        Write-Warning "Failed to mark email as read: $($_.Exception.Message)"
                    }
                }
            }
        } catch {
            Write-Error "Failed to process item: $($_.Exception.Message)"
        }
    }

    # Stop the stopwatch
    $stopwatch.Stop()

    Write-Host "`nDownload Summary:" -ForegroundColor Cyan
    Write-Host "----------------"
    Write-Host "Processed emails: $processedCount"
    Write-Host "Emails with attachments: $downloadedEmails" 
    Write-Host "Downloaded attachments: $downloadedCount" 
    Write-Host "Location: $DownloadPath (organized by date folders)"
    Write-Host "Elapsed time: $($stopwatch.Elapsed)"
}
catch {
    Write-Error "Error: $_"
}
finally {
    # Clean up COM objects
    if ($null -ne $outlook) {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($outlook) | Out-Null
    }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
}