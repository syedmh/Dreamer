namespace Combination
{
    public partial class MainForm : Form
    {
        private int[] currentCombination = new int[3];
        private int[] targetCombination = new int[3];
        private int attemptCount = 0;

        public MainForm()
        {
            InitializeComponent();
            ResetCombination();
        }

        private void StartButton_Click(object sender, EventArgs e)
        {
            // Get target combination from input
            targetCombination[0] = (int)target1Input.Value;
            targetCombination[1] = (int)target2Input.Value;
            targetCombination[2] = (int)target3Input.Value;

            // Reset current combination and counter
            ResetCombination();
            attemptCount = 0;

            // Update UI
            statusLabel.Text = "Searching...";
            statusLabel.ForeColor = System.Drawing.Color.Orange;
            startButton.Enabled = false;
            stopButton.Enabled = true;
            target1Input.Enabled = false;
            target2Input.Enabled = false;
            target3Input.Enabled = false;

            // Start the search timer
            searchTimer.Start();
        }

        private void StopButton_Click(object sender, EventArgs e)
        {
            // Stop the search
            searchTimer.Stop();
            statusLabel.Text = "Stopped";
            statusLabel.ForeColor = System.Drawing.Color.Red;
            startButton.Enabled = true;
            stopButton.Enabled = false;
            target1Input.Enabled = true;
            target2Input.Enabled = true;
            target3Input.Enabled = true;
        }

        private void ResetButton_Click(object sender, EventArgs e)
        {
            // Stop timer if running
            searchTimer.Stop();

            // Reset combination
            ResetCombination();
            attemptCount = 0;
            UpdateDisplay();

            // Reset UI
            statusLabel.Text = "Ready";
            statusLabel.ForeColor = System.Drawing.Color.Blue;
            attemptsLabel.Text = "Attempts: 0";
            startButton.Enabled = true;
            stopButton.Enabled = false;
            target1Input.Enabled = true;
            target2Input.Enabled = true;
            target3Input.Enabled = true;
        }

        private void SearchTimer_Tick(object sender, EventArgs e)
        {
            // Update display with current combination
            UpdateDisplay();

            // Increment attempt counter
            attemptCount++;
            attemptsLabel.Text = $"Attempts: {attemptCount}";

            // Check if we found the combination
            if (CheckCombination())
            {
                // Found it!
                searchTimer.Stop();
                statusLabel.Text = "Found!";
                statusLabel.ForeColor = System.Drawing.Color.Green;
                startButton.Enabled = true;
                stopButton.Enabled = false;
                target1Input.Enabled = true;
                target2Input.Enabled = true;
                target3Input.Enabled = true;

                MessageBox.Show(
                    $"Combination found: {currentCombination[0]}-{currentCombination[1]}-{currentCombination[2]}\n" +
                    $"Attempts: {attemptCount}",
                    "Success!",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                // Not found, increment to next combination
                IncrementCombination();
            }
        }

        private void ResetCombination()
        {
            currentCombination[0] = 0;
            currentCombination[1] = 0;
            currentCombination[2] = 0;
            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            spinner1Label.Text = currentCombination[0].ToString();
            spinner2Label.Text = currentCombination[1].ToString();
            spinner3Label.Text = currentCombination[2].ToString();
        }

        private bool CheckCombination()
        {
            return currentCombination[0] == targetCombination[0] &&
                   currentCombination[1] == targetCombination[1] &&
                   currentCombination[2] == targetCombination[2];
        }

        private void IncrementCombination()
        {
            // Increment rightmost spinner (spinner 3)
            currentCombination[2]++;

            // Handle overflow - cascade to left
            if (currentCombination[2] > 9)
            {
                currentCombination[2] = 0;
                currentCombination[1]++;

                if (currentCombination[1] > 9)
                {
                    currentCombination[1] = 0;
                    currentCombination[0]++;

                    if (currentCombination[0] > 9)
                    {
                        // All combinations exhausted (shouldn't happen with valid target)
                        currentCombination[0] = 0;
                        searchTimer.Stop();
                        statusLabel.Text = "Not Found";
                        statusLabel.ForeColor = System.Drawing.Color.Red;
                        MessageBox.Show(
                            "All combinations tried without finding the target!",
                            "Search Complete",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }
        }
    }
}
