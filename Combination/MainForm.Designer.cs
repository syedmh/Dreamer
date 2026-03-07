namespace Combination
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            // Spinner Display Labels
            this.spinner1Label = new System.Windows.Forms.Label();
            this.spinner2Label = new System.Windows.Forms.Label();
            this.spinner3Label = new System.Windows.Forms.Label();

            // Target Input Controls
            this.targetLabel = new System.Windows.Forms.Label();
            this.target1Input = new System.Windows.Forms.NumericUpDown();
            this.target2Input = new System.Windows.Forms.NumericUpDown();
            this.target3Input = new System.Windows.Forms.NumericUpDown();

            // Control Buttons
            this.startButton = new System.Windows.Forms.Button();
            this.stopButton = new System.Windows.Forms.Button();
            this.resetButton = new System.Windows.Forms.Button();

            // Status Labels
            this.statusLabel = new System.Windows.Forms.Label();
            this.attemptsLabel = new System.Windows.Forms.Label();

            // Timer
            this.searchTimer = new System.Windows.Forms.Timer(this.components);

            ((System.ComponentModel.ISupportInitialize)(this.target1Input)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.target2Input)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.target3Input)).BeginInit();
            this.SuspendLayout();

            //
            // spinner1Label
            //
            this.spinner1Label.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(64)))), ((int)(((byte)(64)))), ((int)(((byte)(64)))));
            this.spinner1Label.Font = new System.Drawing.Font("Consolas", 72F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.spinner1Label.ForeColor = System.Drawing.Color.White;
            this.spinner1Label.Location = new System.Drawing.Point(50, 30);
            this.spinner1Label.Name = "spinner1Label";
            this.spinner1Label.Size = new System.Drawing.Size(120, 120);
            this.spinner1Label.TabIndex = 0;
            this.spinner1Label.Text = "0";
            this.spinner1Label.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.spinner1Label.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;

            //
            // spinner2Label
            //
            this.spinner2Label.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(64)))), ((int)(((byte)(64)))), ((int)(((byte)(64)))));
            this.spinner2Label.Font = new System.Drawing.Font("Consolas", 72F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.spinner2Label.ForeColor = System.Drawing.Color.White;
            this.spinner2Label.Location = new System.Drawing.Point(190, 30);
            this.spinner2Label.Name = "spinner2Label";
            this.spinner2Label.Size = new System.Drawing.Size(120, 120);
            this.spinner2Label.TabIndex = 1;
            this.spinner2Label.Text = "0";
            this.spinner2Label.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.spinner2Label.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;

            //
            // spinner3Label
            //
            this.spinner3Label.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(64)))), ((int)(((byte)(64)))), ((int)(((byte)(64)))));
            this.spinner3Label.Font = new System.Drawing.Font("Consolas", 72F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.spinner3Label.ForeColor = System.Drawing.Color.White;
            this.spinner3Label.Location = new System.Drawing.Point(330, 30);
            this.spinner3Label.Name = "spinner3Label";
            this.spinner3Label.Size = new System.Drawing.Size(120, 120);
            this.spinner3Label.TabIndex = 2;
            this.spinner3Label.Text = "0";
            this.spinner3Label.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.spinner3Label.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;

            //
            // targetLabel
            //
            this.targetLabel.AutoSize = true;
            this.targetLabel.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.targetLabel.Location = new System.Drawing.Point(50, 180);
            this.targetLabel.Name = "targetLabel";
            this.targetLabel.Size = new System.Drawing.Size(210, 21);
            this.targetLabel.TabIndex = 3;
            this.targetLabel.Text = "Set Target Combination:";

            //
            // target1Input
            //
            this.target1Input.Font = new System.Drawing.Font("Segoe UI", 20F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.target1Input.Location = new System.Drawing.Point(50, 210);
            this.target1Input.Maximum = new decimal(new int[] { 9, 0, 0, 0 });
            this.target1Input.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            this.target1Input.Name = "target1Input";
            this.target1Input.Size = new System.Drawing.Size(80, 43);
            this.target1Input.TabIndex = 4;
            this.target1Input.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            this.target1Input.Value = new decimal(new int[] { 0, 0, 0, 0 });

            //
            // target2Input
            //
            this.target2Input.Font = new System.Drawing.Font("Segoe UI", 20F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.target2Input.Location = new System.Drawing.Point(150, 210);
            this.target2Input.Maximum = new decimal(new int[] { 9, 0, 0, 0 });
            this.target2Input.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            this.target2Input.Name = "target2Input";
            this.target2Input.Size = new System.Drawing.Size(80, 43);
            this.target2Input.TabIndex = 5;
            this.target2Input.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            this.target2Input.Value = new decimal(new int[] { 0, 0, 0, 0 });

            //
            // target3Input
            //
            this.target3Input.Font = new System.Drawing.Font("Segoe UI", 20F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.target3Input.Location = new System.Drawing.Point(250, 210);
            this.target3Input.Maximum = new decimal(new int[] { 9, 0, 0, 0 });
            this.target3Input.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            this.target3Input.Name = "target3Input";
            this.target3Input.Size = new System.Drawing.Size(80, 43);
            this.target3Input.TabIndex = 6;
            this.target3Input.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            this.target3Input.Value = new decimal(new int[] { 0, 0, 0, 0 });

            //
            // startButton
            //
            this.startButton.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(192)))), ((int)(((byte)(0)))));
            this.startButton.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.startButton.ForeColor = System.Drawing.Color.White;
            this.startButton.Location = new System.Drawing.Point(50, 280);
            this.startButton.Name = "startButton";
            this.startButton.Size = new System.Drawing.Size(120, 50);
            this.startButton.TabIndex = 7;
            this.startButton.Text = "Start";
            this.startButton.UseVisualStyleBackColor = false;
            this.startButton.Click += new System.EventHandler(this.StartButton_Click);

            //
            // stopButton
            //
            this.stopButton.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(192)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))));
            this.stopButton.Enabled = false;
            this.stopButton.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.stopButton.ForeColor = System.Drawing.Color.White;
            this.stopButton.Location = new System.Drawing.Point(190, 280);
            this.stopButton.Name = "stopButton";
            this.stopButton.Size = new System.Drawing.Size(120, 50);
            this.stopButton.TabIndex = 8;
            this.stopButton.Text = "Stop";
            this.stopButton.UseVisualStyleBackColor = false;
            this.stopButton.Click += new System.EventHandler(this.StopButton_Click);

            //
            // resetButton
            //
            this.resetButton.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(64)))), ((int)(((byte)(64)))), ((int)(((byte)(64)))));
            this.resetButton.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.resetButton.ForeColor = System.Drawing.Color.White;
            this.resetButton.Location = new System.Drawing.Point(330, 280);
            this.resetButton.Name = "resetButton";
            this.resetButton.Size = new System.Drawing.Size(120, 50);
            this.resetButton.TabIndex = 9;
            this.resetButton.Text = "Reset";
            this.resetButton.UseVisualStyleBackColor = false;
            this.resetButton.Click += new System.EventHandler(this.ResetButton_Click);

            //
            // statusLabel
            //
            this.statusLabel.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.statusLabel.ForeColor = System.Drawing.Color.Blue;
            this.statusLabel.Location = new System.Drawing.Point(50, 350);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(400, 30);
            this.statusLabel.TabIndex = 10;
            this.statusLabel.Text = "Ready";
            this.statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;

            //
            // attemptsLabel
            //
            this.attemptsLabel.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.attemptsLabel.Location = new System.Drawing.Point(50, 390);
            this.attemptsLabel.Name = "attemptsLabel";
            this.attemptsLabel.Size = new System.Drawing.Size(400, 25);
            this.attemptsLabel.TabIndex = 11;
            this.attemptsLabel.Text = "Attempts: 0";
            this.attemptsLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;

            //
            // searchTimer
            //
            this.searchTimer.Interval = 50;
            this.searchTimer.Tick += new System.EventHandler(this.SearchTimer_Tick);

            //
            // MainForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(500, 440);
            this.Controls.Add(this.attemptsLabel);
            this.Controls.Add(this.statusLabel);
            this.Controls.Add(this.resetButton);
            this.Controls.Add(this.stopButton);
            this.Controls.Add(this.startButton);
            this.Controls.Add(this.target3Input);
            this.Controls.Add(this.target2Input);
            this.Controls.Add(this.target1Input);
            this.Controls.Add(this.targetLabel);
            this.Controls.Add(this.spinner3Label);
            this.Controls.Add(this.spinner2Label);
            this.Controls.Add(this.spinner1Label);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Combination Lock Cracker";
            ((System.ComponentModel.ISupportInitialize)(this.target1Input)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.target2Input)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.target3Input)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label spinner1Label;
        private System.Windows.Forms.Label spinner2Label;
        private System.Windows.Forms.Label spinner3Label;
        private System.Windows.Forms.Label targetLabel;
        private System.Windows.Forms.NumericUpDown target1Input;
        private System.Windows.Forms.NumericUpDown target2Input;
        private System.Windows.Forms.NumericUpDown target3Input;
        private System.Windows.Forms.Button startButton;
        private System.Windows.Forms.Button stopButton;
        private System.Windows.Forms.Button resetButton;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Label attemptsLabel;
        private System.Windows.Forms.Timer searchTimer;
    }
}
