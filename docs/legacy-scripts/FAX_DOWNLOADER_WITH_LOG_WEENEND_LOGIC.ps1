Function Find-Folder {
    param (
        [Parameter(Mandatory=$true)]
        [object]$startFolder,
        [string]$targetFolderName
    )

    if ($startFolder.Name -eq $targetFolderName) {
        return $startFolder
    }

    foreach ($subFolder in $startFolder.Folders) {
        $foundFolder = Find-Folder -startFolder $subFolder -targetFolderName $targetFolderName
        if ($foundFolder -ne $null) {
            return $foundFolder
        }
    }
    return $null
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

Function Get-NextBusinessDay {
    param (
        [datetime]$date
    )
    
    $nextBusinessDay = $date

    # If the email is received after 3 PM on a Friday, or on a weekend, set the date to the following Monday
    if ($date.DayOfWeek -eq [System.DayOfWeek]::Friday -and $date.Hour -ge 15) {
        $nextBusinessDay = $date.AddDays((8 - [int]$date.DayOfWeek) % 7)
    } elseif ($date.DayOfWeek -eq [System.DayOfWeek]::Saturday) {
        $nextBusinessDay = $date.AddDays(2)
    } elseif ($date.DayOfWeek -eq [System.DayOfWeek]::Sunday) {
        $nextBusinessDay = $date.AddDays(1)
    }

    return $nextBusinessDay
}

Function Download-Attachments {
    # Pass your own mailbox and shares — the defaults below are placeholders,
    # not a live account or location.
    param (
        [string]$rootFolderName = "user@example.com",
        [string]$targetFolderName = "FAXTRUNK",
        [string]$saveRootPath = "\\SERVER\SHARE\_FAXTRUNK PROCESSING\DOWNLOADED_FAX",
        [string]$csvFolderPath = "\\SERVER\SHARE\_FAXTRUNK PROCESSING\__LOGS\DOWNLOAD LOGS",
        [int]$TimeoutMinutes = 15  # Specify the timeout period in minutes
    )

    if (-not $csvFolderPath) {
        # Prompt the user to select a folder if not provided
        $csvFolderPath = [System.Windows.Forms.FolderBrowserDialog]::new()
        $null = $csvFolderPath.ShowDialog()
        $csvFolderPath = $csvFolderPath.SelectedPath
        if (-not $csvFolderPath) {
            Write-Error "No CSV folder path provided or selected. Exiting."
            return
        }
    }

    try {
        # Initialize Outlook application
        $outlook = New-Object -ComObject Outlook.Application
        $namespace = $outlook.GetNamespace("MAPI")
    } catch {
        Write-Error "Failed to initialize Outlook application."
        return
    }
    
    try {
        # Set the root folder (e.g., your email account folder)
        $rootFolder = $namespace.Folders.Item($rootFolderName)
        if (-not $rootFolder) {
            throw "Root folder '$rootFolderName' not found."
        }

        # Find the target folder recursively
        $targetFolder = Find-Folder -startFolder $rootFolder -targetFolderName $targetFolderName
        if ($targetFolder -eq $null) {
            throw "The folder '$targetFolderName' was not found."
        }
    } catch {
        Write-Error $_
        return
    }

    # Ensure the CSV folder path exists
    if (-not (Test-Path -Path $csvFolderPath)) {
        New-Item -ItemType Directory -Path $csvFolderPath | Out-Null
    }

    # Ensure the saveRootPath exists
    if (-not (Test-Path -Path $saveRootPath)) {
        New-Item -ItemType Directory -Path $saveRootPath | Out-Null
    }

    # Initialize CSV file
    $logFileName = (Get-Date).ToString("yyyyMMdd") + "-FAX-DOWNLOAD-LOG.csv"
    $logFilePath = Join-Path -Path $csvFolderPath -ChildPath $logFileName
    $csvHeaders = @("DATE", "TIME", "ATTACHMENT NAME", "PROVIDER FAX NUMBER")

    # Create CSV file with headers if it doesn't exist
    if (-not (Test-Path -Path $logFilePath)) {
        $csvHeaders -join ',' | Out-File -FilePath $logFilePath -Encoding UTF8
    } else {
        # Verify the existing CSV file's headers
        $existingHeaders = (Get-Content -Path $logFilePath -First 1)
        if ($existingHeaders -ne ($csvHeaders -join ',')) {
            Write-Error "Existing CSV headers do not match expected headers. Please check the CSV file."
            return
        }
    }

    # Helper function to create a PSCustomObject with the correct properties
    function New-LogEntry {
        param (
            [string]$Date,
            [string]$Time,
            [string]$AttachmentName,
            [string]$ProviderFaxNumber
        )
        return [pscustomobject]@{
            DATE = $Date
            TIME = $Time
            'ATTACHMENT NAME' = $AttachmentName
            'PROVIDER FAX NUMBER' = $ProviderFaxNumber
        }
    }

    # Start the stopwatch to track elapsed time
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    # Loop through all items in the folder
    foreach ($item in $targetFolder.Items) {
        # Check if the timeout has been reached
        if ($stopwatch.Elapsed.TotalMinutes -ge $TimeoutMinutes) {
            Write-Host "Timeout reached. Ending script."
            break
        }

        try {
            # Check if the item is an email and not already downloaded
            if ($item.Class -eq 43 -and ($item.Categories -notmatch "Downloaded")) {  # 43 corresponds to olMail
                # Extract the fax number from the email body
                $body = $item.Body
                $faxNumberMatch = [regex]::Match($body, "You have received a new \d+ page fax from \((?<AreaCode>\d{3})\)(?<FaxNumber>\d{3}-\d{4})")
                $faxNumber = if ($faxNumberMatch.Success) { "($($faxNumberMatch.Groups["AreaCode"].Value))$($faxNumberMatch.Groups["FaxNumber"].Value)" } else { "" }

                if ($faxNumber) {
                    # Format the email date and time, adjusting for business days
                    $receivedTime = $item.ReceivedTime
                    $nextBusinessDay = Get-NextBusinessDay -date $receivedTime
                    $emailDate = $nextBusinessDay.ToString("yyyyMMdd")
                    $emailTime = $nextBusinessDay.ToString("HH:mm:ss")

                    # Loop through all attachments in the email
                    foreach ($attachment in $item.Attachments) {
                        # Create the new filename with the extra dash
                        $fileName = "${emailDate}--$($attachment.FileName)"
                        # Save the attachment
                        $attachment.SaveAsFile((Join-Path -Path $saveRootPath -ChildPath $fileName))

                        # Log the details to CSV
                        $logEntry = New-LogEntry -Date $emailDate -Time $emailTime -AttachmentName $fileName -ProviderFaxNumber $faxNumber
                        Write-Retry -ScriptBlock { 
                            $logEntry | Export-Csv -Path $logFilePath -Append -NoTypeInformation 
                        }
                    }

                    # Check if the item already has the "Downloaded" category
                    if ($item.Categories -notcontains "Downloaded") {
                        # Add the "Downloaded" category to the email
                        $item.Categories += "Downloaded"
                    }

                    # Mark the email as read
                    $item.UnRead = $false
                    $item.Save()
                }
            }
        } catch {
            Write-Error "Failed to process item: $($_.Exception.Message)"
        }
    }

    # Stop the stopwatch
    $stopwatch.Stop()

    Write-Host "Script completed or timeout reached. Elapsed time: $($stopwatch.Elapsed)"
}

Download-Attachments
